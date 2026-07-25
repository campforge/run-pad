using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Build.Core.Models;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using RunPad.Services;
using RunPad.Windows;
using TextMateSharp.Grammars;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #62 "ILSpy-обозреватель сборок" (user request 2026-07-19; merged with the standalone
// DecompiledViewWindow per user request 2026-07-20) — AssemblyExplorerWindow: file picker +
// namespace/type tree on the left, a read-only decompiled source pane on the right that updates IN
// PLACE on double-click (no popped-up child window, dnSpy/ILSpy don't do that either). Also the
// landing window for Go to Definition (F12) on a symbol outside the user's script — opened straight
// to that member decompiled, with its assembly's tree loaded alongside it.
//
// Проход 134 (CI-cost pass) — ExplorerCode used to get a fresh real `dotnet build` (~1.5s) in each of
// 3 tests; they only ever READ the resulting binary, so it's compiled once via IClassFixture and
// shared. RecursiveCode below is still built inline — only one test needs it, so there's nothing to
// share.
public class AssemblyExplorerWindowTests : IClassFixture<AssemblyExplorerWindowTests.CompiledExplorerAssembly>
{
    private readonly CompiledExplorerAssembly _explorerAssembly;

    public AssemblyExplorerWindowTests(CompiledExplorerAssembly explorerAssembly)
    {
        _explorerAssembly = explorerAssembly;
    }

    private static Mock<IThemeService> CreateThemeService()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        return theme;
    }

    private static MsBuildProcessCompiler CreateCompiler(string outputDir)
    {
        var sdk = new DotnetSdkProvider().Get().First();
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();
        return new MsBuildProcessCompiler(
            CSharpProjectProfile.Instance,
            sdk: sdk,
            executionOptions: new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            outputDir: outputDir,
            runPadRuntimeLib: runtimeLib,
            outputName: "Query",
            nugetPackages: new Dictionary<string, string>(),
            usings: [],
            assemblyReferences: [],
            projectReferences: [],
            logger: NullLogger<MsBuildProcessCompiler>.Instance);
    }

    private const string ExplorerCode = """
        _ = 0;

        namespace Foo.Bar
        {
            public class Alpha { }
        }
        namespace Foo.Baz
        {
            public class Beta { }
        }
        """;

    public sealed class CompiledExplorerAssembly : IAsyncLifetime
    {
        public string BinaryPath { get; private set; } = null!;

        private string? _outputDir;

        public async ValueTask InitializeAsync()
        {
            _outputDir = Directory.CreateTempSubdirectory("runpad-assembly-explorer-test").FullName;
            using var compiler = CreateCompiler(_outputDir);
            var result = await compiler.Compilation(ExplorerCode, CancellationToken.None);
            if (!result.Success)
                throw new InvalidOperationException(
                    "Shared fixture compilation failed: " + string.Join('\n', result.Errors.Select(e => e.Message)));
            BinaryPath = result.OutputBinaryFile!;
        }

        public ValueTask DisposeAsync()
        {
            if (_outputDir is not null)
                Directory.Delete(_outputDir, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task LoadAssemblyAsync_Should_BuildATree_GroupedByNamespace_OneNodePerType()
    {
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "");
        window.Show();

        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);

        Assert.Equal(_explorerAssembly.BinaryPath, window.AssemblyPathText.Text);
        // Not asserting the exact node count — the top-level `_ = 0;` statement compiles into its
        // own implicit `Program` type in the global namespace (a real, ILSpy-visible type, not
        // compiler-generated noise — see EnumerateTypes' own filtering rationale), which would
        // make this test brittle for a reason unrelated to what it actually checks.
        var namespaceNodes = window.Tree.Items.OfType<TreeViewItem>().ToList();
        var barNode = namespaceNodes.Single(n => Equals(n.Header, "Foo.Bar"));
        var typeNode = barNode.Items.OfType<TreeViewItem>().Single();
        Assert.Equal("Alpha", typeNode.Header);
    }

    [AvaloniaFact]
    public async Task LoadAssemblyAsync_Should_ShowAnErrorDialog_AndNotThrow_WhenTheFileIsNotAnAssembly()
    {
        var notAnAssembly = Path.Combine(
            Directory.CreateTempSubdirectory("runpad-assembly-explorer-test").FullName, "notadll.dll");
        await File.WriteAllTextAsync(notAnAssembly, "not a real assembly");
        try
        {
            var windowService = new Mock<IWindowService>();
            windowService.Setup(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
            var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "");
            window.Show();

            await window.LoadAssemblyAsync(notAnAssembly);

            windowService.Verify(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            Assert.Empty(window.Tree.Items.OfType<TreeViewItem>());
        }
        finally { Directory.Delete(Path.GetDirectoryName(notAnAssembly)!, recursive: true); }
    }

    [AvaloniaFact]
    public async Task OpenTypeAsync_Should_DecompileInPlace_ForTheSelectedType()
    {
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "");
        window.Show();
        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);

        var types = CSharpDecompilerService.EnumerateTypes(_explorerAssembly.BinaryPath);
        var alpha = types.Single(t => t.Name == "Alpha");

        await window.OpenTypeAsync(alpha);

        // No child window opened — this is the whole point of the merge (user request
        // 2026-07-20): the SAME window's editor pane updates in place, no new popup per type.
        Assert.Empty(window.OwnedWindows);
        Assert.Contains("class Alpha", window.Editor.Text);
        Assert.Equal("Alpha", window.Title);
    }

    // ── Go to Definition (F12) entry point — was DecompiledViewWindowTests.cs before the merge ──

    [AvaloniaFact]
    public void FromInitialResult_Should_ShowReadOnlyDecompiledText_WithTitleAndCaretOnTheRequestedLine()
    {
        const string code = "class Foo\n{\n    void Bar() { }\n}\n";
        var result = new DecompiledMemberResult(code, Line: 3, Title: "Foo.Bar()");
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, result, assemblyPath: null);
        window.Show();

        Assert.Equal("Foo.Bar()", window.Title);
        Assert.True(window.Editor.IsReadOnly);
        Assert.Equal(code, window.Editor.Text);
        Assert.Equal(3, window.Editor.TextArea.Caret.Line);
        // assemblyPath: null (the "type not found" fallback shape) — no tree to load, and no crash
        // attempting to load one either.
        Assert.Empty(window.Tree.Items.OfType<TreeViewItem>());
    }

    [AvaloniaFact]
    public async Task FromInitialResult_Should_AlsoLoadTheAssemblysTree_WhenAnAssemblyPathIsGiven()
    {
        var initialResult = CSharpDecompilerService.DecompileType(
            _explorerAssembly.BinaryPath, "Foo.Bar.Alpha", displayName: "Alpha");
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(
            CreateThemeService().Object, windowService.Object, initialResult, _explorerAssembly.BinaryPath);
        window.Show();

        for (var i = 0; i < 100 && window.Tree.Items.Count == 0; i++)
            await Task.Delay(20);

        Assert.Equal("Alpha", window.Title);
        var namespaceNodes = window.Tree.Items.OfType<TreeViewItem>().ToList();
        Assert.Contains(namespaceNodes, n => Equals(n.Header, "Foo.Baz"));
    }

    [AvaloniaFact]
    public async Task NavigateAsync_Should_DoNothing_WhenTheInitialResultCarriesNoSyntaxTree()
    {
        // The "type not found" fallback result from CSharpDecompilerService.Decompile has no tree
        // behind it (nothing was actually decompiled) — F12 here must be a no-op, not a crash.
        const string code = "// Could not find type 'Foo'.";
        var result = new DecompiledMemberResult(code, Line: 1, Title: "Foo");
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, result, assemblyPath: null);
        window.Show();

        await window.NavigateAsync();

        Assert.Equal("Foo", window.Title);
        Assert.Equal(code, window.Editor.Text);
    }

    private const string RecursiveCode = """
        Caller.DoWork();
        class Caller
        {
            public static void DoWork() { Callee.Helper(); }
        }
        class Callee
        {
            public static void Helper() { System.Console.WriteLine("helper"); }
        }
        """;

    [AvaloniaFact]
    public async Task NavigateAsync_Should_JumpToTheCalleesDeclaration_WhenCaretIsOnACallSite()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-assembly-explorer-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var compilation = await compiler.Compilation(RecursiveCode, CancellationToken.None);
            Assert.True(compilation.Success, string.Join('\n', compilation.Errors.Select(e => e.Message)));

            var callerResult = CSharpDecompilerService.DecompileType(compilation.OutputBinaryFile!, "Caller");
            var windowService = new Mock<IWindowService>();
            var window = new AssemblyExplorerWindow(
                CreateThemeService().Object, windowService.Object, callerResult, assemblyPath: null);
            window.Show();

            var callLine = window.Editor.Text.Split('\n').ToList().FindIndex(l => l.Contains("Helper")) + 1;
            var callColumn = window.Editor.Text.Split('\n')[callLine - 1].IndexOf("Helper", StringComparison.Ordinal) + 1;
            window.Editor.TextArea.Caret.Line = callLine;
            window.Editor.TextArea.Caret.Column = callColumn;

            await window.NavigateAsync();

            Assert.Contains("Callee", window.Title);
            Assert.Contains("Helper", window.Editor.Text.Split('\n')[window.Editor.TextArea.Caret.Line - 1]);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
