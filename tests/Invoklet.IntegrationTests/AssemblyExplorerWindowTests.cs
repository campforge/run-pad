using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Invoklet.Avalonia.CodeTextEditor.Highlighting;
using Invoklet.Avalonia.Themes;
using Invoklet.Build.Core.Models;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TextMateSharp.Grammars;
using Xunit;

namespace Invoklet.IntegrationTests;

// ILSpy-style assembly explorer (merged with the former standalone
// DecompiledViewWindow) — AssemblyExplorerWindow: file picker +
// namespace/type tree on the left, a read-only decompiled source pane on the right that updates IN
// PLACE on double-click (no popped-up child window, dnSpy/ILSpy don't do that either). Also the
// landing window for Go to Definition (F12) on a symbol outside the user's script — opened straight
// to that member decompiled, with its assembly's tree loaded alongside it.
//
// Pass 134 (CI-cost pass) — ExplorerCode used to get a fresh real `dotnet build` (~1.5s) in each of
// 3 tests; they only ever READ the resulting binary, so it's compiled once via IClassFixture and
// shared. RecursiveCode below is still built inline — only one test needs it, so there's nothing to
// share.
[Trait("Category", "Integration")]
[Trait("Category", "Decompiler")]
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

    // The script compiler Invoklet itself uses (in-process Roslyn) — a real assembly to explore.
    private static CSharpScriptCompiler CreateCompiler(string outputDir) => new(
        new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdkProvider().Get().First(),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = outputDir,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
        },
        NullLogger.Instance);

    private const string ExplorerCode = """
        _ = 0;

        namespace Foo.Bar
        {
            public class Alpha
            {
                private int _count;
                public void Run() { _count++; }
            }
            public class Gamma : Alpha
            {
                public void Call() { Run(); }
            }
        }
        namespace Foo.Baz
        {
            public class Beta { }
            public abstract class Delta
            {
                public static int Zero;
                public abstract void Go();
            }
            public class Epsilon : Delta { public override void Go() { } }
            public static class DeltaUser { public static void Use(Delta delta) => delta.Go(); }
        }
        """;

    public sealed class CompiledExplorerAssembly : IAsyncLifetime
    {
        public string BinaryPath { get; private set; } = null!;

        private string? _outputDir;

        public async ValueTask InitializeAsync()
        {
            _outputDir = Directory.CreateTempSubdirectory("Invoklet-assembly-explorer-test").FullName;
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();

        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);

        Assert.Equal(_explorerAssembly.BinaryPath, window.AssemblyPathText.Text);
        // Not asserting the exact node count — the top-level `_ = 0;` statement compiles into its
        // own implicit `Program` type in the global namespace (a real, ILSpy-visible type, not
        // compiler-generated noise — see EnumerateTypes' own filtering rationale), which would
        // make this test brittle for a reason unrelated to what it actually checks.
        var namespaceNodes = Namespaces(window);
        var barNode = namespaceNodes.Single(n => Text(n) == "Foo.Bar");
        Assert.Equal(["Alpha", "Gamma"], barNode.Items.OfType<TreeViewItem>().Select(Text));
        Assert.Equal(AssemblyNodeKind.Namespace, Node(barNode).Kind);
        Assert.NotNull(Node(barNode).Icon);
    }

    private static AssemblyTreeNode Node(TreeViewItem item) => Assert.IsType<AssemblyTreeNode>(item.Header);

    private static string? Text(TreeViewItem item) => (item.Header as AssemblyTreeNode)?.Text;

    // The tree's top level is assemblies (ILSpy's assembly list); namespaces sit under the first one.
    private static TreeViewItem AssemblyRow(AssemblyExplorerWindow window, int index = 0) =>
        window.Tree.Items.OfType<TreeViewItem>().ElementAt(index);

    private static List<TreeViewItem> Namespaces(AssemblyExplorerWindow window) =>
        AssemblyRow(window).Items.OfType<TreeViewItem>().Where(i => Node(i).Kind == AssemblyNodeKind.Namespace).ToList();

    private static TreeViewItem Namespace(AssemblyExplorerWindow window, string name) =>
        Namespaces(window).Single(n => Text(n) == name);

    private async Task<TreeViewItem> LoadAndFindAlphaAsync(AssemblyExplorerWindow window)
    {
        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);
        var barNode = Namespace(window, "Foo.Bar");
        return barNode.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");
    }

    // Opens Gamma.Call() (which calls Alpha.Run()) and F12s on the call — the ILSpy round trip.
    private async Task<(AssemblyExplorerWindow Window, TreeViewItem Call)> OpenCallAndNavigateToRunAsync()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);
        var barNode = Namespace(window, "Foo.Bar");
        var gamma = barNode.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Gamma");
        await window.ExpandAsync(gamma);
        var call = gamma.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Call() : void");
        await window.SelectNodeAsync(Node(call), call);

        var lines = window.Editor.Text.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("Run()")) + 1;
        var column = lines[line - 1].IndexOf("Run", StringComparison.Ordinal) + 1;
        await window.NavigateAtAsync(line, column);
        return (window, call);
    }

    [AvaloniaFact]
    public async Task NavigateAsync_Should_RevealAndSelectTheTargetMember_InTheTree()
    {
        var (window, _) = await OpenCallAndNavigateToRunAsync();

        var selected = Assert.IsType<TreeViewItem>(window.Tree.SelectedItem);
        Assert.Equal("Run() : void", Text(selected));
        Assert.Equal("Alpha", Text((TreeViewItem)selected.Parent!));
        Assert.True(((TreeViewItem)selected.Parent!).IsExpanded);
        Assert.Equal("Run() : void", window.Title);
        Assert.Contains("void Run()", window.Editor.Text);
        Assert.DoesNotContain("class Alpha", window.Editor.Text); // the member alone, like a click
    }

    [AvaloniaFact]
    public async Task CtrlClick_OnAReference_Should_NavigateToIt()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);
        var gamma = Namespace(window, "Foo.Bar")
            .Items.OfType<TreeViewItem>().Single(n => Text(n) == "Gamma");
        await window.ExpandAsync(gamma);
        var call = gamma.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Call() : void");
        await window.SelectNodeAsync(Node(call), call);
        Dispatcher.UIThread.RunJobs();

        var lines = window.Editor.Text.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("Run()")) + 1;
        var column = lines[line - 1].IndexOf("Run", StringComparison.Ordinal) + 2; // inside "Run"
        var textView = window.Editor.TextArea.TextView;
        var visual = textView.GetVisualPosition(new TextViewPosition(line, column), VisualYPosition.LineMiddle)
                     - new Vector(textView.HorizontalOffset, textView.VerticalOffset);
        var point = textView.TranslatePoint(visual, window)!.Value;

        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        await TestWait.WhileAsync(() => window.Title != "Run() : void");

        Assert.Equal("Run() : void", Text((TreeViewItem)window.Tree.SelectedItem!));
    }

    private static readonly DecompilerView IlView =
        new(DecompilerLanguage.IL, ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest);

    [AvaloniaFact]
    public async Task SetViewAsync_IL_Should_ReRenderTheCurrentPage_InPlace()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.ExpandAsync(alpha);
        var run = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Run() : void");
        await window.SelectNodeAsync(Node(run), run);

        await window.SetViewAsync(IlView);

        Assert.Equal("Run() : void", window.Title);
        Assert.Contains(".method", window.Editor.Text);
        Assert.Contains("IL_0000", window.Editor.Text);
        Assert.False(window.CanGoBack); // a re-render, not a new page
    }

    // ── IL syntax highlighting of the code pane ──

    private static readonly ActiveTheme LightEditorTheme = new(
        new InterfaceTheme("Light", ThemeVariant.Light, false), new EditorTheme("Light+", ThemeName.LightPlus, false));

    private static int IndexOf(AssemblyExplorerWindow window, string text) =>
        window.Editor.Text.IndexOf(text, StringComparison.Ordinal);

    [AvaloniaFact]
    public async Task CodePane_Should_UseTheIlGrammarInTheIlView_AndTheCSharpGrammarAgain()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.SelectNodeAsync(Node(alpha), alpha);
        Assert.Equal("source.cs", window.GrammarScope);
        var csharp = await IlHighlightingTests.SettledColorAsync(window.Editor, IndexOf(window, "class"),
            c => c is { } color && color != Colors.Black);

        await window.SetViewAsync(IlView);

        Assert.Equal(IlGrammar.ScopeName, window.GrammarScope);
        var directive = await IlHighlightingTests.SettledColorAsync(window.Editor, IndexOf(window, ".class"),
            c => c is { } color && color != Colors.Black);
        Assert.NotNull(directive);

        await window.SetViewAsync(DecompilerView.Default);

        Assert.Equal("source.cs", window.GrammarScope);
        Assert.Equal(csharp, await IlHighlightingTests.SettledColorAsync(window.Editor, IndexOf(window, "class"), c => c == csharp));
        Assert.DoesNotContain(".class", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task ThemeChange_Should_RecolourTheIlView_AndKeepItsGrammar()
    {
        var theme = CreateThemeService();
        var window = new AssemblyExplorerWindow(theme.Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.SelectNodeAsync(Node(alpha), alpha);
        await window.SetViewAsync(IlView);
        var method = IndexOf(window, ".class");
        var dark = await IlHighlightingTests.SettledColorAsync(window.Editor, method, c => c is { } color && color != Colors.Black);
        var text = window.Editor.Text;

        theme.Raise(t => t.ThemeChanged += null, LightEditorTheme);

        var light = await IlHighlightingTests.SettledColorAsync(window.Editor, method, c => c is { } color && color != dark);
        Assert.NotEqual(dark, light);
        Assert.Equal(IlGrammar.ScopeName, window.GrammarScope);
        Assert.Equal(text, window.Editor.Text);

        await window.SetViewAsync(DecompilerView.Default); // the new palette applies to C# as well
        Assert.Equal("source.cs", window.GrammarScope);
    }

    // ── Large assemblies: the tree is prepared in slices, off a blocking UI pass ──

    private sealed class SmallSlices : IDisposable
    {
        private readonly int _previous = AssemblyExplorerWindow.TreeBuildSliceSize;

        public SmallSlices(int size) => AssemblyExplorerWindow.TreeBuildSliceSize = size;

        public void Dispose() => AssemblyExplorerWindow.TreeBuildSliceSize = _previous;
    }

    private AssemblyExplorerWindow ShowWindow(Mock<IThemeService>? theme = null)
    {
        var window = new AssemblyExplorerWindow((theme ?? CreateThemeService()).Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        return window;
    }

    private string CopyOfExplorerAssembly()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-second-assembly").FullName;
        var copy = Path.Combine(folder, "Second.dll");
        File.Copy(_explorerAssembly.BinaryPath, copy);
        return copy;
    }

    // Opens `slow` first, held after its first slice until released, then runs `whileSlowLoads`.
    private static async Task WithSlowLoadAsync(
        AssemblyExplorerWindow window, string slow, Func<Task> whileSlowLoads, Func<Task>? afterRelease = null)
    {
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var held = false;
        window.TreeSliceBuilt = async slice =>
        {
            if (held) return;
            held = true;
            reached.SetResult();
            await release.Task;
        };
        var slowOpen = window.OpenAssemblyAsync(slow);
        await reached.Task;
        window.TreeSliceBuilt = null;

        await whileSlowLoads();
        release.SetResult();
        await slowOpen;
        if (afterRelease is not null) await afterRelease();
    }

    // ── Search ──

    // The Windows/Linux defaults on every OS, so the Ctrl/Alt keys the tests press are the bound ones.
    private static IPreferencesService WindowsLinuxKeys(bool mac = false)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings { IsMac = mac });
        return prefs.Object;
    }

    private async Task<AssemblyExplorerWindow> OpenLoadedWindowAsync()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);
        return window;
    }

    private static List<AssemblyTreeNode> Results(AssemblyExplorerWindow window) =>
        window.SearchResultList.Items.OfType<AssemblyTreeNode>().ToList();

    [AvaloniaFact]
    public async Task SearchAsync_Should_ListMatchingMembers_WithIconAndLocation()
    {
        var window = await OpenLoadedWindowAsync();
        var panel = window.FindControl<StackPanel>("SearchPanel")!;
        Assert.False(panel.IsVisible);

        await window.SearchAsync("run");

        var hit = Assert.Single(Results(window));
        Assert.Equal("Run() : void", hit.Text);
        Assert.Equal("Foo.Bar.Alpha", hit.Location);
        Assert.NotNull(hit.Icon);
        Assert.True(panel.IsVisible);
        Assert.Equal("1 result", window.FindControl<TextBlock>("SearchStatus")!.Text);
    }

    // ── Several assemblies, References ──

    private static TreeViewItem ReferencesRow(TreeViewItem assemblyRow) =>
        assemblyRow.Items.OfType<TreeViewItem>().Single(i => Node(i).Kind == AssemblyNodeKind.References);

    // ── Analyze ──

    private async Task<(AssemblyExplorerWindow Window, TreeViewItem Run)> OpenWithRunRowAsync()
    {
        var window = await OpenLoadedWindowAsync();
        var alpha = Namespace(window, "Foo.Bar").Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");
        await window.ExpandAsync(alpha);
        return (window, alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Run() : void"));
    }

    private static List<AssemblyTreeNode> AnalysisHits(AssemblyExplorerWindow window) =>
        window.AnalysisResultList.Items.OfType<AssemblyTreeNode>().ToList();

    [AvaloniaFact]
    public async Task AnalyzeAsync_UsedBy_Should_ShowTheCallers_InTheAnalysisPane()
    {
        var (window, run) = await OpenWithRunRowAsync();
        var pane = window.FindControl<Border>("AnalysisPanel")!;
        Assert.False(pane.IsVisible);

        await window.AnalyzeAsync(Node(run), AnalysisKind.UsedBy);

        Assert.True(pane.IsVisible);
        Assert.Equal("Used By: Run() : void", window.FindControl<TextBlock>("AnalysisTitle")!.Text);
        Assert.Equal("1 result", window.FindControl<TextBlock>("AnalysisStatus")!.Text);
        var hit = Assert.Single(AnalysisHits(window));
        Assert.Equal("Call() : void", hit.Text);
        Assert.Equal("Foo.Bar.Gamma", hit.Location);
        Assert.NotNull(hit.Icon);
    }

    // ── Tabs ──

    private async Task<(AssemblyExplorerWindow Window, TreeViewItem Alpha, TreeViewItem Run)> OpenAlphaAndRunRowsAsync()
    {
        var (window, run) = await OpenWithRunRowAsync();
        var alpha = (TreeViewItem)run.Parent!;
        return (window, alpha, run);
    }

    // ── Resources ──

    [AvaloniaFact]
    public async Task AResourceRow_Should_ShowTheResource_UnderAResourcesNode()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-resources-test").FullName;
        try
        {
            var dll = Path.Combine(folder, "WithResources.dll");
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("WithResources",
                [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("public class C { }")],
                [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            using (var pe = File.Create(dll))
            {
                var emitted = compilation.Emit(pe, manifestResources:
                [
                    new Microsoft.CodeAnalysis.ResourceDescription("readme.txt",
                        () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello resources")), isPublic: true),
                ]);
                Assert.True(emitted.Success);
            }
            var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
            window.Show();
            await window.LoadAssemblyAsync(dll);

            var resourcesRow = AssemblyRow(window).Items.OfType<TreeViewItem>().Single(i => Node(i).Kind == AssemblyNodeKind.Resources);
            var readme = Assert.Single(resourcesRow.Items.OfType<TreeViewItem>());
            Assert.Equal("readme.txt", Text(readme));
            Assert.NotNull(Node(readme).Icon);

            await window.SelectNodeAsync(Node(readme), readme);

            Assert.Equal("readme.txt", window.Title);
            Assert.Equal("// readme.txt (15 bytes)\n\nhello resources", window.Editor.Text);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ── Drag & drop, Ctrl+hover ──

    [AvaloniaFact]
    public async Task UpdateReferenceHover_Should_UnderlineOnlyANavigableIdentifier()
    {
        var window = await OpenLoadedWindowAsync();
        var gamma = Namespace(window, "Foo.Bar").Items.OfType<TreeViewItem>().Single(n => Text(n) == "Gamma");
        await window.ExpandAsync(gamma);
        var call = gamma.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Call() : void");
        await window.SelectNodeAsync(Node(call), call);
        var lines = window.Editor.Text.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("Run()")) + 1;
        var column = lines[line - 1].IndexOf("Run", StringComparison.Ordinal) + 1;

        window.UpdateReferenceHover((line, column + 1));

        Assert.Equal(new ReferenceSpan(line, column, line, column + "Run".Length), window.HoveredReference);
        Assert.NotNull(window.Editor.TextArea.TextView.Cursor);

        window.UpdateReferenceHover((line, 1)); // leading whitespace
        Assert.Null(window.HoveredReference);
        Assert.Null(window.Editor.TextArea.TextView.Cursor);

        window.UpdateReferenceHover((line, column + 1));
        window.UpdateReferenceHover(null); // Ctrl released
        Assert.Null(window.HoveredReference);
    }

    // ── Save Code ──

    private async Task<(AssemblyExplorerWindow Window, Mock<IWindowService> Service)> OpenRunPageAsync(string? savePath)
    {
        var service = new Mock<IWindowService>();
        service.Setup(s => s.ShowSaveCodeFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(savePath);
        service.Setup(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, service.Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.ExpandAsync(alpha);
        var run = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Run() : void");
        await window.SelectNodeAsync(Node(run), run);
        return (window, service);
    }

    [AvaloniaFact]
    public async Task SaveCodeAsync_Should_WriteTheShownCSharp_ToACsFile_NamedAfterThePage()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-save-code-test").FullName;
        try
        {
            var path = Path.Combine(folder, "Run.cs");
            var (window, service) = await OpenRunPageAsync(path);

            await window.SaveCodeAsync();

            service.Verify(s => s.ShowSaveCodeFileDialogAsync("Run", "cs", It.IsAny<string>()), Times.Once);
            Assert.Equal(window.Editor.Text, await File.ReadAllTextAsync(path));
            Assert.Contains("void Run()", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task SelectingATreeItem_Should_DecompileIt()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);

        window.Tree.SelectedItem = alpha;

        await TestWait.WhileAsync(() => window.Title != "Alpha");
        Assert.Contains("class Alpha", window.Editor.Text);
    }

    // ── Go to Definition (F12) entry point — was DecompiledViewWindowTests.cs before the merge ──

    [AvaloniaFact]
    public async Task FromInitialResult_Should_AlsoLoadTheAssemblysTree_WhenAnAssemblyPathIsGiven()
    {
        var initialResult = CSharpDecompilerService.DecompileType(
            _explorerAssembly.BinaryPath, "Foo.Bar.Alpha", displayName: "Alpha");
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(
            CreateThemeService().Object, windowService.Object, initialResult, _explorerAssembly.BinaryPath);
        window.Show();

        await TestWait.WhileAsync(() => window.Tree.Items.Count == 0);

        Assert.Equal("Alpha", window.Title);
        var namespaceNodes = Namespaces(window);
        Assert.Contains(namespaceNodes, n => Text(n) == "Foo.Baz");
    }

    // F12 from the user's script: the definition carries names only (DecompiledReference stands in for
    // the IntelliSense result; its tokens aren't used by this entry point).
    private AssemblyExplorerWindow OpenFromScriptF12(string typeName, string? memberName, string display)
    {
        var definition = new DecompiledReference(
            _explorerAssembly.BinaryPath, typeName, memberName, [], display, MetadataToken: 0, ContainingTypeMetadataToken: 0);
        var initial = CSharpDecompilerService.Decompile(definition);
        var window = new AssemblyExplorerWindow(
            CreateThemeService().Object, new Mock<IWindowService>().Object, initial, _explorerAssembly.BinaryPath, definition);
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public async Task FromScriptF12_Should_RevealAndSelectTheMembersRow_KeepingTheWholeTypePage()
    {
        var window = OpenFromScriptF12("Foo.Bar.Alpha", "Run", "Foo.Bar.Alpha.Run");

        await TestWait.WhileAsync(() => window.Tree.SelectedItem is null);

        var selected = Assert.IsType<TreeViewItem>(window.Tree.SelectedItem);
        Assert.Equal("Run() : void", Text(selected));
        var alpha = (TreeViewItem)selected.Parent!;
        Assert.Equal("Alpha", Text(alpha));
        Assert.True(alpha.IsExpanded);
        Assert.Equal("Foo.Bar.Alpha.Run", window.Title); // the page F12 opened stays: the whole type
        Assert.Contains("class Alpha", window.Editor.Text);
        Assert.Contains("Run", window.Editor.Text.Split('\n')[window.Editor.TextArea.Caret.Line - 1]);
    }

    [AvaloniaFact]
    public async Task FromScriptF12_OnAType_Should_SelectTheTypesRow_AndThePage_Should_ReRenderInIl()
    {
        var window = OpenFromScriptF12("Foo.Bar.Gamma", memberName: null, "Foo.Bar.Gamma");

        await TestWait.WhileAsync(() => window.Tree.SelectedItem is null);
        Assert.Equal("Gamma", Text((TreeViewItem)window.Tree.SelectedItem!));

        await window.SetViewAsync(IlView); // the page now has a target, so it can be rendered again

        Assert.Contains(".class", window.Editor.Text);
        Assert.Contains("Gamma", window.Editor.Text);
    }

    private DecompiledReference ScriptDefinition(string typeName, string? memberName) => new(
        _explorerAssembly.BinaryPath, typeName, memberName, [], memberName is null ? typeName : $"{typeName}.{memberName}",
        MetadataToken: 0, ContainingTypeMetadataToken: 0);

    [AvaloniaFact]
    public async Task ALaterScriptF12_Should_OpenInANewTab_AndSelectItsRow()
    {
        var window = OpenFromScriptF12("Foo.Bar.Alpha", "Run", "Foo.Bar.Alpha.Run");
        await TestWait.WhileAsync(() => window.Tree.SelectedItem is null);
        var gamma = ScriptDefinition("Foo.Bar.Gamma", memberName: null);

        await window.OpenDefinitionInNewTabAsync(CSharpDecompilerService.Decompile(gamma), gamma.AssemblyPath, gamma);

        Assert.Equal(2, window.TabCount);
        Assert.Equal(1, window.ActiveTabIndex);
        Assert.Equal("Foo.Bar.Gamma", window.Title);
        Assert.Equal("Gamma", Text((TreeViewItem)window.Tree.SelectedItem!));
        Assert.Single(window.LoadedAssemblyPaths); // the same assembly isn't added twice

        window.ActivateNextTab(-1); // the first tab kept its page and its row
        Assert.Equal("Foo.Bar.Alpha.Run", window.Title);
        Assert.Equal("Run() : void", Text((TreeViewItem)window.Tree.SelectedItem!));
    }

    [AvaloniaFact]
    public async Task AScriptF12_IntoAnEmptyExplorer_Should_UseItsOnlyTab()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys() };
        window.Show();
        var alpha = ScriptDefinition("Foo.Bar.Alpha", memberName: null);

        await window.OpenDefinitionInNewTabAsync(CSharpDecompilerService.Decompile(alpha), alpha.AssemblyPath, alpha);

        Assert.Equal(1, window.TabCount);
        Assert.Equal("Alpha", Text((TreeViewItem)window.Tree.SelectedItem!));
    }

    // F12's page is decompiled as default C#: in an explorer already in IL it's re-rendered as IL once
    // its target is known, and Save Code writes that IL to an .il file.
    [AvaloniaFact]
    public async Task ALaterScriptF12_InIlView_Should_ShowItsPageAsIl_AndSaveItAsIl()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-save-code-test").FullName;
        var path = Path.Combine(folder, "Gamma.il");
        var service = new Mock<IWindowService>();
        service.Setup(s => s.ShowSaveCodeFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        var run = ScriptDefinition("Foo.Bar.Alpha", "Run");
        var window = new AssemblyExplorerWindow(
            CreateThemeService().Object, service.Object, CSharpDecompilerService.Decompile(run), run.AssemblyPath, run);
        try
        {
            window.Show();
            await TestWait.WhileAsync(() => window.Tree.SelectedItem is null);
            await window.SetViewAsync(IlView);
            var gamma = ScriptDefinition("Foo.Bar.Gamma", memberName: null);

            await window.OpenDefinitionInNewTabAsync(CSharpDecompilerService.Decompile(gamma), gamma.AssemblyPath, gamma);

            Assert.Equal(1, window.ActiveTabIndex);
            Assert.Equal("Foo.Bar.Gamma", window.Title);
            Assert.Equal("Gamma", Text((TreeViewItem)window.Tree.SelectedItem!));
            Assert.Contains(".class", window.Editor.Text);
            Assert.DoesNotContain("public class Gamma", window.Editor.Text);
            await window.SaveCodeAsync();
            service.Verify(s => s.ShowSaveCodeFileDialogAsync(
                AssemblyExplorerWindow.SuggestedFileName("Foo.Bar.Gamma"), "il", It.IsAny<string>()), Times.Once);
            var saved = await File.ReadAllTextAsync(path);
            Assert.Contains(".class", saved);
            Assert.DoesNotContain("public class Gamma", saved);

            await window.SetViewAsync(DecompilerView.Default); // a real target behind it: back to C#
            Assert.Contains("public class Gamma", window.Editor.Text);
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    // A view switch while the first F12 page has no target yet can't re-render it — it does once known.
    [AvaloniaFact]
    public async Task SwitchingTheView_BeforeTheFirstScriptF12IsRevealed_Should_ReRenderItOnceItsTargetIsKnown()
    {
        var window = OpenFromScriptF12("Foo.Bar.Gamma", memberName: null, "Foo.Bar.Gamma");
        try
        {
            Assert.Null(window.Tree.SelectedItem); // not revealed yet
            await window.SetViewAsync(IlView);
            Assert.Contains("public class Gamma", window.Editor.Text);

            await TestWait.WhileAsync(() => window.Tree.SelectedItem is null);
            await TestWait.WhileAsync(() => !window.Editor.Text.Contains(".class"));

            Assert.Contains(".class", window.Editor.Text);
            Assert.DoesNotContain("public class Gamma", window.Editor.Text);
            Assert.Equal("Foo.Bar.Gamma", window.Title);
            Assert.Equal("Gamma", Text((TreeViewItem)window.Tree.SelectedItem!));
            Assert.False(window.CanGoBack); // re-rendered in place, not a new page
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowService_Should_KeepOneExplorer_ForTheMenuAndEveryScriptF12_UntilItIsClosed()
    {
        var service = new WindowService(
            dependenciesWindowFactory: () => throw new InvalidOperationException("not expected here"),
            preferencesWindowFactory: () => throw new InvalidOperationException("not expected here"),
            themeService: CreateThemeService().Object);
        var alpha = ScriptDefinition("Foo.Bar.Alpha", "Run");
        var gamma = ScriptDefinition("Foo.Bar.Gamma", memberName: null);

        await service.ShowDefinitionInAssemblyExplorerAsync(CSharpDecompilerService.Decompile(alpha), alpha);
        var explorer = Assert.IsType<AssemblyExplorerWindow>(service.AssemblyExplorer);
        await service.ShowDefinitionInAssemblyExplorerAsync(CSharpDecompilerService.Decompile(gamma), gamma);
        await service.ShowAssemblyExplorerAsync("");

        Assert.Same(explorer, service.AssemblyExplorer);
        Assert.Equal(2, explorer.TabCount);

        explorer.Close();
        Assert.Null(service.AssemblyExplorer);
        await service.ShowAssemblyExplorerAsync("");
        Assert.NotSame(explorer, service.AssemblyExplorer);
        service.AssemblyExplorer!.Close();
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
    // The window was opened with no tree (assemblyPath: null): like ILSpy, F12 adds the target's
    // assembly to the tree, reveals the callee there and shows it on its own.
    public async Task NavigateAsync_Should_LoadTheTargetsAssembly_AndOpenTheCallee_WhenCaretIsOnACallSite()
    {
        var outputDir = Directory.CreateTempSubdirectory("Invoklet-assembly-explorer-test").FullName;
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

            Assert.Empty(window.LoadedAssemblyPaths);

            await window.NavigateAsync();

            Assert.Equal([compilation.OutputBinaryFile!], window.LoadedAssemblyPaths);
            Assert.Equal("Helper() : void", window.Title);
            Assert.Contains("// Callee", window.Editor.Text); // declaring-type header
            Assert.Contains("Helper", window.Editor.Text.Split('\n')[window.Editor.TextArea.Caret.Line - 1]);
            var selected = Assert.IsType<TreeViewItem>(window.Tree.SelectedItem);
            Assert.Equal("Helper() : void", Text(selected));
            Assert.Equal("Callee", Text((TreeViewItem)selected.Parent!));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
