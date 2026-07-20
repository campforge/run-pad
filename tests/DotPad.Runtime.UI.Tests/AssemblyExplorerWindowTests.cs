using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DotPad.Avalonia.Themes;
using DotPad.Build.Core.Models;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.DotnetCommon.Compiler;
using DotPad.Services;
using DotPad.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TextMateSharp.Grammars;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "ILSpy-обозреватель сборок" (user request 2026-07-19; merged with the standalone
// DecompiledViewWindow per user request 2026-07-20) — AssemblyExplorerWindow: file picker +
// namespace/type tree on the left, a read-only decompiled source pane on the right that updates IN
// PLACE on double-click (no popped-up child window, dnSpy/ILSpy don't do that either). Also the
// landing window for Go to Definition (F12) on a symbol outside the user's script — opened straight
// to that member decompiled, with its assembly's tree loaded alongside it.
public class AssemblyExplorerWindowTests
{
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
            dotPadRuntimeLib: runtimeLib,
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

    [AvaloniaFact]
    public async Task LoadAssemblyAsync_Should_BuildATree_GroupedByNamespace_OneNodePerType()
    {
        var outputDir = Directory.CreateTempSubdirectory("dotpad-assembly-explorer-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var compilation = await compiler.Compilation(ExplorerCode, CancellationToken.None);
            Assert.True(compilation.Success, string.Join('\n', compilation.Errors.Select(e => e.Message)));

            var windowService = new Mock<IWindowService>();
            var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "");
            window.Show();

            await window.LoadAssemblyAsync(compilation.OutputBinaryFile!);

            Assert.Equal(compilation.OutputBinaryFile, window.AssemblyPathText.Text);
            // Not asserting the exact node count — the top-level `_ = 0;` statement compiles into its
            // own implicit `Program` type in the global namespace (a real, ILSpy-visible type, not
            // compiler-generated noise — see EnumerateTypes' own filtering rationale), which would
            // make this test brittle for a reason unrelated to what it actually checks.
            var namespaceNodes = window.Tree.Items.OfType<TreeViewItem>().ToList();
            var barNode = namespaceNodes.Single(n => Equals(n.Header, "Foo.Bar"));
            var typeNode = barNode.Items.OfType<TreeViewItem>().Single();
            Assert.Equal("Alpha", typeNode.Header);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [AvaloniaFact]
    public async Task LoadAssemblyAsync_Should_ShowAnErrorDialog_AndNotThrow_WhenTheFileIsNotAnAssembly()
    {
        var notAnAssembly = Path.Combine(
            Directory.CreateTempSubdirectory("dotpad-assembly-explorer-test").FullName, "notadll.dll");
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
        var outputDir = Directory.CreateTempSubdirectory("dotpad-assembly-explorer-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var compilation = await compiler.Compilation(ExplorerCode, CancellationToken.None);
            Assert.True(compilation.Success, string.Join('\n', compilation.Errors.Select(e => e.Message)));

            var windowService = new Mock<IWindowService>();
            var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "");
            window.Show();
            await window.LoadAssemblyAsync(compilation.OutputBinaryFile!);

            var types = CSharpDecompilerService.EnumerateTypes(compilation.OutputBinaryFile!);
            var alpha = types.Single(t => t.Name == "Alpha");

            await window.OpenTypeAsync(alpha);

            // No child window opened — this is the whole point of the merge (user request
            // 2026-07-20): the SAME window's editor pane updates in place, no new popup per type.
            Assert.Empty(window.OwnedWindows);
            Assert.Contains("class Alpha", window.Editor.Text);
            Assert.Equal("Alpha", window.Title);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
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
        var outputDir = Directory.CreateTempSubdirectory("dotpad-assembly-explorer-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            var compilation = await compiler.Compilation(ExplorerCode, CancellationToken.None);
            Assert.True(compilation.Success, string.Join('\n', compilation.Errors.Select(e => e.Message)));

            var initialResult = CSharpDecompilerService.DecompileType(
                compilation.OutputBinaryFile!, "Foo.Bar.Alpha", displayName: "Alpha");
            var windowService = new Mock<IWindowService>();
            var window = new AssemblyExplorerWindow(
                CreateThemeService().Object, windowService.Object, initialResult, compilation.OutputBinaryFile);
            window.Show();

            for (var i = 0; i < 100 && window.Tree.Items.Count == 0; i++)
                await Task.Delay(20);

            Assert.Equal("Alpha", window.Title);
            var namespaceNodes = window.Tree.Items.OfType<TreeViewItem>().ToList();
            Assert.Contains(namespaceNodes, n => Equals(n.Header, "Foo.Baz"));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
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
        var outputDir = Directory.CreateTempSubdirectory("dotpad-assembly-explorer-test").FullName;
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
