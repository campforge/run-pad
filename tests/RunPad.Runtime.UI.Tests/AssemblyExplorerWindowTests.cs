using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Build.Core.Models;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Compiler;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using RunPad.Services;
using RunPad.Windows;
using RunPad.Windows.Models;
using TextMateSharp.Grammars;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #62 "ILSpy assembly explorer" (user request 2026-07-19; merged with the standalone
// DecompiledViewWindow per user request 2026-07-20) — AssemblyExplorerWindow: file picker +
// namespace/type tree on the left, a read-only decompiled source pane on the right that updates IN
// PLACE on double-click (no popped-up child window, dnSpy/ILSpy don't do that either). Also the
// landing window for Go to Definition (F12) on a symbol outside the user's script — opened straight
// to that member decompiled, with its assembly's tree loaded alongside it.
//
// Pass 134 (CI-cost pass) — ExplorerCode used to get a fresh real `dotnet build` (~1.5s) in each of
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

    // The script compiler RunPad itself uses (in-process Roslyn) — a real assembly to explore.
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

    [AvaloniaFact]
    public async Task ExpandAsync_Should_ListBaseAndDerivedTypes_ThenMembers_WithIconsAndGreyedNonPublic()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);

        await window.ExpandAsync(alpha);

        var children = alpha.Items.OfType<TreeViewItem>().ToList();
        Assert.Equal(
            ["Base Types", "Derived Types", "_count : int", "Alpha()", "Run() : void"],
            children.Select(Text));
        Assert.All(children, c => Assert.NotNull(Node(c).Icon));

        var count = Node(children[2]);
        Assert.Equal(AssemblyNodeKind.Field, count.Kind);
        Assert.False(count.IsPublic);
        Assert.True(count.Opacity < 1);
        Assert.Equal(1.0, Node(children[4]).Opacity);
        Assert.NotNull(count.AccessOverlay); // private → lock
        Assert.Null(count.StaticOverlay);
        Assert.Null(Node(children[4]).AccessOverlay); // public → no overlay

        Assert.Equal(["object"], children[0].Items.OfType<TreeViewItem>().Select(Text));
        Assert.Equal(["Gamma"], children[1].Items.OfType<TreeViewItem>().Select(Text));
    }

    [AvaloniaFact]
    public async Task BaseTypeEntries_Should_Expand_IntoTheBaseTypesOwnMembers()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.ExpandAsync(alpha);
        var baseTypes = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Base Types");
        var objectItem = baseTypes.Items.OfType<TreeViewItem>().Single();

        await window.ExpandAsync(objectItem);

        Assert.Contains(objectItem.Items.OfType<TreeViewItem>(), i => Text(i)?.StartsWith("ToString()") == true);
    }

    // Opens Gamma.Call() (which calls Alpha.Run()) and F12s on the call — the ILSpy round trip.
    private async Task<(AssemblyExplorerWindow Window, TreeViewItem Call)> OpenCallAndNavigateToRunAsync()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
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
    public async Task GoBack_And_GoForward_Should_RestoreTheCode_AndTheTreeSelection()
    {
        var (window, call) = await OpenCallAndNavigateToRunAsync();
        Assert.True(window.CanGoBack);
        Assert.False(window.CanGoForward);

        window.GoBack();

        Assert.Equal("Call() : void", window.Title);
        Assert.Contains("Run();", window.Editor.Text);
        Assert.Same(call, window.Tree.SelectedItem);
        Assert.True(window.CanGoForward);

        window.GoForward();

        Assert.Equal("Run() : void", window.Title);
        Assert.Equal("Run() : void", Text((TreeViewItem)window.Tree.SelectedItem!));
        Assert.False(window.CanGoForward);
    }

    [AvaloniaFact]
    public async Task CtrlClick_OnAReference_Should_NavigateToIt()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
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

    [AvaloniaFact]
    public async Task InIlView_ANewSelection_AndGoingBack_Should_BothShowIl()
    {
        var (window, _) = await OpenCallAndNavigateToRunAsync(); // Call, then Run — both rendered as C#
        await window.SetViewAsync(IlView);
        var alpha = Namespace(window, "Foo.Bar")
            .Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");

        await window.SelectNodeAsync(Node(alpha), alpha);
        Assert.Contains(".class", window.Editor.Text);

        window.GoBack(); // Run(), rendered as C# before the switch
        await TestWait.WhileAsync(() => !window.Editor.Text.Contains(".method"));

        Assert.Equal("Run() : void", window.Title);
        Assert.Contains(".method", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task LanguageBox_Should_SwitchTheView_AndHideTheVersionBoxForIl()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.SelectNodeAsync(Node(alpha), alpha);
        var languageBox = window.FindControl<ComboBox>("LanguageBox")!;
        var versionBox = window.FindControl<ComboBox>("VersionBox")!;
        Assert.True(versionBox.IsVisible);

        languageBox.SelectedIndex = 1;
        await TestWait.WhileAsync(() => !window.Editor.Text.Contains(".class"));

        Assert.Equal(DecompilerLanguage.IL, window.View.Language);
        Assert.Contains(".class", window.Editor.Text);
        Assert.False(versionBox.IsVisible);
    }

    // ── Search ──

    private async Task<AssemblyExplorerWindow> OpenLoadedWindowAsync()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
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

    [AvaloniaFact]
    public async Task SearchAsync_Should_HideTheResults_ForAnEmptyQuery_AndSayWhenNothingMatches()
    {
        var window = await OpenLoadedWindowAsync();
        var panel = window.FindControl<StackPanel>("SearchPanel")!;

        await window.SearchAsync("NoSuchName");
        Assert.True(panel.IsVisible);
        Assert.Empty(Results(window));
        Assert.Equal("No results", window.FindControl<TextBlock>("SearchStatus")!.Text);

        await window.SearchAsync("  ");
        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public async Task OpenSearchResultAsync_Should_RevealTheHitInTheTree_AndDecompileIt()
    {
        var window = await OpenLoadedWindowAsync();
        await window.SearchAsync("run");

        await window.OpenSearchResultAsync(Results(window).Single());

        var selected = Assert.IsType<TreeViewItem>(window.Tree.SelectedItem);
        Assert.Equal("Run() : void", Text(selected));
        Assert.Equal("Alpha", Text((TreeViewItem)selected.Parent!));
        Assert.Equal("Run() : void", window.Title);
        Assert.Contains("void Run()", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task TypingInTheSearchBox_AndPickingAResult_Should_OpenIt()
    {
        var window = await OpenLoadedWindowAsync();
        var searchBox = window.FindControl<TextBox>("SearchBox")!;

        searchBox.Text = "Gamm";
        await TestWait.WhileAsync(() => Results(window).Count == 0);
        Assert.Contains(Results(window), r => r is { Text: "Gamma", Location: "Foo.Bar" });

        window.SearchResultList.SelectedItem = Results(window).First(r => r.Text == "Gamma");
        await TestWait.WhileAsync(() => window.Title != "Gamma");

        Assert.Contains("class Gamma", window.Editor.Text);
        Assert.Equal("Gamma", Text((TreeViewItem)window.Tree.SelectedItem!));
    }

    [AvaloniaFact]
    public async Task CtrlShiftF_Should_FocusTheSearchBox()
    {
        var window = await OpenLoadedWindowAsync();
        var searchBox = window.FindControl<TextBox>("SearchBox")!;

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.True(searchBox.IsFocused);
    }

    // ── Several assemblies, References ──

    private static TreeViewItem ReferencesRow(TreeViewItem assemblyRow) =>
        assemblyRow.Items.OfType<TreeViewItem>().Single(i => Node(i).Kind == AssemblyNodeKind.References);

    [AvaloniaFact]
    public async Task AssemblyRow_Should_ComeFirst_WithReferences_ThenNamespaces()
    {
        var window = await OpenLoadedWindowAsync();

        var row = AssemblyRow(window);
        Assert.Equal(AssemblyNodeKind.Assembly, Node(row).Kind);
        Assert.Matches(@"^\S+ \(\d+\.\d+\.\d+\.\d+\)$", Node(row).Text); // "Name (1.0.0.0)"
        Assert.NotNull(Node(row).Icon);
        var children = row.Items.OfType<TreeViewItem>().ToList();
        Assert.Equal(AssemblyNodeKind.References, Node(children[0]).Kind);
        Assert.All(children.Skip(1), c => Assert.Equal(AssemblyNodeKind.Namespace, Node(c).Kind));
        var runtime = ReferencesRow(row).Items.OfType<TreeViewItem>().Single(r => Text(r) == "System.Runtime");
        Assert.NotNull(Node(runtime).Reference!.ResolvedPath);
        Assert.Equal(1.0, Node(runtime).Opacity);
    }

    [AvaloniaFact]
    public async Task SelectingTheAssemblyRow_Should_ShowItsAttributes_OrItsManifestInIl()
    {
        var window = await OpenLoadedWindowAsync();
        var row = AssemblyRow(window);

        await window.SelectNodeAsync(Node(row), row);
        Assert.Equal(Node(row).Text, window.Title);
        Assert.Contains("[assembly:", window.Editor.Text);

        await window.SetViewAsync(IlView);
        Assert.Contains(".assembly", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task AReferenceRow_Should_DescribeTheReference_AndOpenItsAssemblyAsASecondRow()
    {
        var window = await OpenLoadedWindowAsync();
        var runtime = ReferencesRow(AssemblyRow(window)).Items.OfType<TreeViewItem>().Single(r => Text(r) == "System.Runtime");

        await window.SelectNodeAsync(Node(runtime), runtime);
        Assert.StartsWith("// System.Runtime, Version=", window.Editor.Text);
        Assert.Contains(Node(runtime).Reference!.ResolvedPath!, window.Editor.Text);

        await window.OpenReferenceAsync(Node(runtime).Reference!);
        await TestWait.WhileAsync(() => window.Title?.StartsWith("System.Runtime", StringComparison.Ordinal) != true);

        Assert.Equal(2, window.LoadedAssemblyPaths.Count);
        var second = AssemblyRow(window, 1);
        Assert.Same(second, window.Tree.SelectedItem);
        Assert.StartsWith("System.Runtime (", Text(second));

        // opening it again finds the same row instead of adding another
        await window.OpenReferenceAsync(Node(runtime).Reference!);
        Assert.Equal(2, window.Tree.Items.Count);
    }

    [AvaloniaFact]
    public async Task LoadAssemblyAsync_Should_NotAddTheSameAssemblyTwice()
    {
        var window = await OpenLoadedWindowAsync();

        var again = await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);

        Assert.Same(AssemblyRow(window), again);
        Assert.Single(window.Tree.Items);
    }

    [AvaloniaFact]
    public async Task RemoveAssembly_Should_DropItsRow_AndItsSearchHits()
    {
        var window = await OpenLoadedWindowAsync();
        var runtime = ReferencesRow(AssemblyRow(window)).Items.OfType<TreeViewItem>().Single(r => Text(r) == "System.Runtime");
        await window.OpenReferenceAsync(Node(runtime).Reference!);
        await window.SearchAsync("Alpha");
        Assert.NotEmpty(Results(window));

        window.RemoveAssembly(AssemblyRow(window, 0));
        await TestWait.WhileAsync(() => Results(window).Count > 0);

        Assert.Single(window.Tree.Items);
        Assert.Single(window.LoadedAssemblyPaths);
        Assert.Empty(Results(window));
    }

    [AvaloniaFact]
    public async Task DeleteKey_OnAnAssemblyRow_Should_RemoveIt()
    {
        var window = await OpenLoadedWindowAsync();
        window.Tree.SelectedItem = AssemblyRow(window);
        AssemblyRow(window).Focus(); // keyboard focus sits on the row, as after a click

        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);

        Assert.Empty(window.Tree.Items);
        Assert.Empty(window.LoadedAssemblyPaths);
    }

    [AvaloniaFact]
    public async Task SearchHits_Should_NameTheirAssembly_OnceThereAreSeveral()
    {
        var window = await OpenLoadedWindowAsync();
        await window.SearchAsync("Alpha");
        Assert.Equal("Foo.Bar", Results(window).Single(r => r.Text == "Alpha").Location);

        var runtime = ReferencesRow(AssemblyRow(window)).Items.OfType<TreeViewItem>().Single(r => Text(r) == "System.Runtime");
        await window.OpenReferenceAsync(Node(runtime).Reference!);
        await window.SearchAsync("Alpha");

        var assemblyName = Text(AssemblyRow(window))!.Split(' ')[0];
        Assert.Equal($"Foo.Bar ({assemblyName})", Results(window).Single(r => r.Text == "Alpha").Location);
    }

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

    [AvaloniaFact]
    public async Task IncludeVirtualCalls_Should_ReRunUsedBy_CountingCallsThroughTheBase()
    {
        var window = await OpenLoadedWindowAsync();
        var epsilon = Namespace(window, "Foo.Baz").Items.OfType<TreeViewItem>().Single(n => Text(n) == "Epsilon");
        await window.ExpandAsync(epsilon);
        var go = epsilon.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Go() : void");
        var box = window.FindControl<CheckBox>("IncludeVirtualCallsBox")!;

        await window.AnalyzeAsync(Node(go), AnalysisKind.UsedBy);
        Assert.True(box.IsVisible);
        Assert.Empty(AnalysisHits(window)); // Use(Delta) calls Delta.Go, not Epsilon.Go

        box.IsChecked = true;
        await TestWait.WhileAsync(() => AnalysisHits(window).Count == 0);

        var hit = Assert.Single(AnalysisHits(window));
        Assert.Equal("Use(Delta) : void", hit.Text);
        Assert.Equal("Foo.Baz.DeltaUser", hit.Location);

        // not offered where it means nothing: a type's Used By
        await window.AnalyzeAsync(Node(epsilon), AnalysisKind.UsedBy);
        Assert.False(box.IsVisible);
    }

    [AvaloniaFact]
    public async Task PickingAnAnalysisHit_Should_RevealAndOpenIt()
    {
        var (window, run) = await OpenWithRunRowAsync();
        await window.AnalyzeAsync(Node(run), AnalysisKind.UsedBy);

        window.AnalysisResultList.SelectedIndex = 0;
        await TestWait.WhileAsync(() => window.Title != "Call() : void");

        Assert.Contains("Run();", window.Editor.Text);
        var selected = Assert.IsType<TreeViewItem>(window.Tree.SelectedItem);
        Assert.Equal("Call() : void", Text(selected));
        Assert.Equal("Gamma", Text((TreeViewItem)selected.Parent!));
    }

    [AvaloniaFact]
    public async Task TheTreeContextMenu_Should_OfferTheRowsAnalyses()
    {
        var (window, run) = await OpenWithRunRowAsync();
        window.Tree.SelectedItem = run;

        window.TreeMenu.Open(window.Tree);

        var headers = window.TreeMenu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        Assert.Equal(["Open in New Tab", "Analyze: Used By", "Analyze: Uses", "Remove assembly"], headers);
        Assert.False(window.TreeMenu.Items.OfType<MenuItem>().Last().IsEnabled); // not an assembly row
        window.TreeMenu.Close();

        // reopened on a field row: the field's analyses replace the method's
        var count = Namespace(window, "Foo.Bar").Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha")
            .Items.OfType<TreeViewItem>().Single(n => Text(n) == "_count : int");
        window.Tree.SelectedItem = count;
        window.TreeMenu.Open(window.Tree);
        Assert.Equal(
            ["Open in New Tab", "Analyze: Read By", "Analyze: Assigned By", "Remove assembly"],
            window.TreeMenu.Items.OfType<MenuItem>().Select(i => i.Header as string));
        window.TreeMenu.Close();
    }

    [AvaloniaFact]
    public async Task CtrlR_Should_RunTheSelectedRowsFirstAnalysis_AndCloseShouldHideThePane()
    {
        var (window, run) = await OpenWithRunRowAsync();
        window.Tree.SelectedItem = run;
        var pane = window.FindControl<Border>("AnalysisPanel")!;

        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);
        await TestWait.WhileAsync(() => AnalysisHits(window).Count == 0);

        Assert.Equal("Used By: Run() : void", window.FindControl<TextBlock>("AnalysisTitle")!.Text);
        Assert.Equal("Call() : void", Assert.Single(AnalysisHits(window)).Text);

        window.FindControl<Button>("CloseAnalysisButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(pane.IsVisible);
    }

    // ── Tabs ──

    private async Task<(AssemblyExplorerWindow Window, TreeViewItem Alpha, TreeViewItem Run)> OpenAlphaAndRunRowsAsync()
    {
        var (window, run) = await OpenWithRunRowAsync();
        var alpha = (TreeViewItem)run.Parent!;
        return (window, alpha, run);
    }

    [AvaloniaFact]
    public async Task OpenInNewTabAsync_Should_AddATab_AndKeepTheFirstTabsPage()
    {
        var (window, alpha, run) = await OpenAlphaAndRunRowsAsync();
        await window.SelectNodeAsync(Node(alpha), alpha);
        Assert.Equal(1, window.TabCount);

        await window.OpenInNewTabAsync(Node(run), run);

        Assert.Equal(2, window.TabCount);
        Assert.Equal(1, window.ActiveTabIndex);
        Assert.Equal(["Alpha", "Run() : void"], window.TabTitles);
        Assert.Equal("Run() : void", window.Title);
        Assert.Same(run, window.Tree.SelectedItem);
        Assert.False(window.CanGoBack); // the new tab has its own (empty) history

        window.ActivateNextTab(+1); // wraps around to the first tab

        Assert.Equal(0, window.ActiveTabIndex);
        Assert.Equal("Alpha", window.Title);
        Assert.Contains("class Alpha", window.Editor.Text);
        Assert.Same(alpha, window.Tree.SelectedItem);
    }

    [AvaloniaFact]
    public async Task EachTab_Should_HaveItsOwnBackForwardHistory()
    {
        var (window, alpha, run) = await OpenAlphaAndRunRowsAsync();
        await window.SelectNodeAsync(Node(alpha), alpha);
        await window.SelectNodeAsync(Node(run), run); // first tab: Alpha → Run
        Assert.True(window.CanGoBack);

        await window.OpenInNewTabAsync(Node(alpha), alpha);
        Assert.False(window.CanGoBack);

        window.ActivateNextTab(-1);
        Assert.Equal("Run() : void", window.Title);
        window.GoBack();
        Assert.Equal("Alpha", window.Title);
        Assert.Equal(2, window.TabCount);
    }

    [AvaloniaFact]
    public async Task CtrlW_Should_CloseTheActiveTab_AndClosingTheLastTab_Should_EmptyIt()
    {
        var (window, alpha, run) = await OpenAlphaAndRunRowsAsync();
        await window.SelectNodeAsync(Node(alpha), alpha);
        await window.OpenInNewTabAsync(Node(run), run);

        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);

        Assert.Equal(1, window.TabCount);
        Assert.Equal("Alpha", window.Title);

        window.CloseActiveTab(); // the last one: emptied, not removed
        Assert.Equal(1, window.TabCount);
        Assert.Equal(["New Tab"], window.TabTitles);
        Assert.Equal("", window.Editor.Text);
        Assert.False(window.FindControl<Button>("SaveCodeButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task MiddleClickOnATab_Should_CloseIt()
    {
        var (window, alpha, run) = await OpenAlphaAndRunRowsAsync();
        await window.SelectNodeAsync(Node(alpha), alpha);
        await window.OpenInNewTabAsync(Node(run), run);
        window.UpdateLayout();
        var firstTab = window.FindControl<ListBox>("DocumentTabs")!.ContainerFromIndex(0)!;
        var center = firstTab.TranslatePoint(new Point(firstTab.Bounds.Width / 2, firstTab.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Middle);
        window.MouseUp(center, MouseButton.Middle);

        Assert.Equal(["Run() : void"], window.TabTitles); // the middle-clicked first tab is gone
        Assert.Equal("Run() : void", window.Title);
    }

    [AvaloniaFact]
    public void ALongTabTitle_Should_BeTrimmed_KeepingTheCloseButtonInsideTheTab()
    {
        // user report: Func<T1, ..., T16, TResult>'s title pushed the × out of the tab
        var title = "Func<" + string.Join(", ", Enumerable.Range(1, 16).Select(i => $"T{i}")) + ", TResult>";
        var window = new AssemblyExplorerWindow(
            CreateThemeService().Object, new Mock<IWindowService>().Object,
            new DecompiledMemberResult("// code", 1, title), assemblyPath: null);
        window.Show();
        window.UpdateLayout();

        var item = window.FindControl<ListBox>("DocumentTabs")!.ContainerFromIndex(0)!;
        var close = item.GetVisualDescendants().OfType<Button>().Single();
        var closeBounds = close.Bounds.Translate(close.TranslatePoint(default, item)!.Value - close.Bounds.Position);
        Assert.True(close.IsEffectivelyVisible);
        Assert.True(closeBounds.Width > 0);
        Assert.True(closeBounds.Right <= item.Bounds.Width, $"× ends at {closeBounds.Right}, the tab at {item.Bounds.Width}");
        Assert.True(item.Bounds.Width < 320, $"the tab is {item.Bounds.Width} wide"); // capped, title trimmed
    }

    [AvaloniaFact]
    public async Task TheTabStrip_Should_ShowTheTabs_AndSwitchOnSelection()
    {
        var (window, alpha, run) = await OpenAlphaAndRunRowsAsync();
        await window.SelectNodeAsync(Node(alpha), alpha);
        await window.OpenInNewTabAsync(Node(run), run);
        var strip = window.FindControl<ListBox>("DocumentTabs")!;
        Assert.Equal(2, strip.ItemCount);
        Assert.Equal(1, strip.SelectedIndex);

        strip.SelectedIndex = 0;

        Assert.Equal(0, window.ActiveTabIndex);
        Assert.Equal("Alpha", window.Title);
    }

    // ── Resources ──

    [AvaloniaFact]
    public async Task AResourceRow_Should_ShowTheResource_UnderAResourcesNode()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-resources-test").FullName;
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
            var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
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
    public async Task OpenDroppedFilesAsync_Should_AddTheAssemblies_AndIgnoreOtherFiles()
    {
        var service = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, service.Object, "");
        window.Show();

        await window.OpenDroppedFilesAsync([_explorerAssembly.BinaryPath, Path.ChangeExtension(_explorerAssembly.BinaryPath, ".txt")]);

        Assert.Equal([_explorerAssembly.BinaryPath], window.LoadedAssemblyPaths);
        Assert.Same(AssemblyRow(window), window.Tree.SelectedItem);
        service.Verify(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

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

    [Theory]
    [InlineData("Run() : void", "Run")]
    [InlineData("Echo<T>(T, ref int) : T", "Echo")]
    [InlineData("Dictionary<TKey, TValue>", "Dictionary")]
    [InlineData("ExplorerFixture (1.0.0.0)", "ExplorerFixture")]
    [InlineData("_count : int", "_count")]
    [InlineData("Callee.Helper", "Callee.Helper")]
    [InlineData("a/b\\c", "a_b_c")]
    [InlineData("   ", "Decompiled")]
    public void SuggestedFileName_Should_KeepTheName_WithoutSignatureOrTypeArguments(string title, string expected)
    {
        var name = AssemblyExplorerWindow.SuggestedFileName(title);

        // '\' is only invalid in file names on Windows
        Assert.Equal(OperatingSystem.IsWindows() ? expected : expected.Replace("b_c", "b\\c"), name);
    }

    private async Task<(AssemblyExplorerWindow Window, Mock<IWindowService> Service)> OpenRunPageAsync(string? savePath)
    {
        var service = new Mock<IWindowService>();
        service.Setup(s => s.ShowSaveCodeFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(savePath);
        service.Setup(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, service.Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.ExpandAsync(alpha);
        var run = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Run() : void");
        await window.SelectNodeAsync(Node(run), run);
        return (window, service);
    }

    [AvaloniaFact]
    public async Task SaveCode_Should_BeDisabled_UntilAPageIsShown()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        Assert.False(window.FindControl<Button>("SaveCodeButton")!.IsEnabled);

        var alpha = await LoadAndFindAlphaAsync(window);
        await window.SelectNodeAsync(Node(alpha), alpha);

        Assert.True(window.FindControl<Button>("SaveCodeButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task SaveCodeAsync_Should_WriteTheShownCSharp_ToACsFile_NamedAfterThePage()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-save-code-test").FullName;
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
    public async Task SaveCodeAsync_InIlView_Should_WriteIl_ToAnIlFile()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-save-code-test").FullName;
        try
        {
            var path = Path.Combine(folder, "Run.il");
            var (window, service) = await OpenRunPageAsync(path);
            await window.SetViewAsync(IlView);

            await window.SaveCodeAsync();

            service.Verify(s => s.ShowSaveCodeFileDialogAsync("Run", "il", It.IsAny<string>()), Times.Once);
            Assert.Contains(".method", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task SaveCodeAsync_Should_DoNothing_WhenTheDialogIsCancelled()
    {
        var (window, service) = await OpenRunPageAsync(savePath: null);

        await window.SaveCodeAsync();

        service.Verify(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task SaveCodeAsync_Should_ShowAnError_WhenTheFileCantBeWritten()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-save-code-test").FullName;
        try
        {
            // a path that is an existing folder can't be written as a file
            var (window, service) = await OpenRunPageAsync(folder);

            await window.SaveCodeAsync();

            service.Verify(s => s.ShowErrorAsync("Save Code Failed", It.IsAny<string>()), Times.Once);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task CtrlS_Should_SaveTheCode()
    {
        var (window, service) = await OpenRunPageAsync(savePath: null);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);

        await TestWait.WhileAsync(() => service.Invocations.All(i => i.Method.Name != nameof(IWindowService.ShowSaveCodeFileDialogAsync)));
        service.Verify(s => s.ShowSaveCodeFileDialogAsync("Run", "cs", It.IsAny<string>()), Times.Once);
    }

    [AvaloniaFact]
    public async Task AltLeft_Should_GoBack()
    {
        var (window, _) = await OpenCallAndNavigateToRunAsync();

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);

        Assert.Equal("Call() : void", window.Title);
    }

    [AvaloniaFact]
    public async Task ANewSelection_Should_DropTheForwardHistory()
    {
        var (window, _) = await OpenCallAndNavigateToRunAsync();
        window.GoBack();
        var alpha = Namespace(window, "Foo.Bar")
            .Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");

        await window.SelectNodeAsync(Node(alpha), alpha);

        Assert.False(window.CanGoForward);
        Assert.True(window.CanGoBack);
    }

    [AvaloniaFact]
    public async Task Tree_Should_ItaliciseAbstractTypesAndMembers_AndMarkStaticMembers()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);
        var bazNode = Namespace(window, "Foo.Baz");
        var delta = bazNode.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Delta");
        var beta = bazNode.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Beta");

        await window.ExpandAsync(delta);

        Assert.Equal(FontStyle.Italic, Node(delta).FontStyle);
        Assert.Equal(FontStyle.Normal, Node(beta).FontStyle);
        var members = delta.Items.OfType<TreeViewItem>().Select(Node).ToList();
        var zero = members.Single(m => m.Text == "Zero : int");
        Assert.NotNull(zero.StaticOverlay);
        Assert.Equal(FontStyle.Normal, zero.FontStyle);
        var go = members.Single(m => m.Text == "Go() : void");
        Assert.Null(go.StaticOverlay);
        Assert.Equal(FontStyle.Italic, go.FontStyle);
        // an abstract class's implicit constructor is protected → key overlay, still not greyed
        var ctor = members.Single(m => m.Kind == AssemblyNodeKind.Constructor);
        Assert.NotNull(ctor.AccessOverlay);
        Assert.Equal(1.0, ctor.Opacity);
    }

    // The icons sit straight on the panel background; every kind colour must keep WCAG's 3:1 non-text
    // contrast against every theme's panel background, light and dark alike (no GUI check needed).
    [AvaloniaFact]
    public void KindIconColours_Should_Contrast_AtLeast3To1_WithEveryThemesPanelBackground()
    {
        var theme = new RunPadTheme();
        var variants = typeof(RunPadThemeVariants).GetFields()
            .Select(f => (ThemeVariant)f.GetValue(null)!)
            .ToList();
        Assert.Equal(8, variants.Count);

        foreach (var variant in variants)
        {
            Assert.True(theme.TryGetResource("RunPad.Panel.BackgroundBrush", variant, out var brush), variant.ToString());
            var background = ((ISolidColorBrush)brush!).Color;
            foreach (var (kind, (_, color)) in AssemblyExplorerWindow.KindIcons)
            {
                var contrast = Contrast(color, background);
                Assert.True(contrast >= 3.0, $"{kind} {color} on {variant} {background}: {contrast:F2}");
            }
        }

        static double Contrast(Color a, Color b)
        {
            var (x, y) = (Luminance(a), Luminance(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }

    [AvaloniaFact]
    public async Task SelectNodeAsync_Should_DecompileOnlyTheSelectedMember_UnderTheHeader()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.ExpandAsync(alpha);
        var run = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Run() : void");

        await window.SelectNodeAsync(Node(run));

        Assert.Equal("Run() : void", window.Title);
        Assert.Contains("// Foo.Bar.Alpha", window.Editor.Text);
        Assert.Contains("void Run()", window.Editor.Text);
        Assert.DoesNotContain("class Alpha", window.Editor.Text);
        Assert.Contains("Run", window.Editor.Text.Split('\n')[window.Editor.TextArea.Caret.Line - 1]);
    }

    [AvaloniaFact]
    public async Task SelectingATreeItem_Should_DecompileIt()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);

        window.Tree.SelectedItem = alpha;

        await TestWait.WhileAsync(() => window.Title != "Alpha");
        Assert.Contains("class Alpha", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task SelectNodeAsync_Should_KeepTheNewestSelection_WhenAnOlderOneFinishesLater()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.ExpandAsync(alpha);
        var run = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Run() : void");

        var older = window.SelectNodeAsync(Node(alpha));
        var newer = window.SelectNodeAsync(Node(run));
        await Task.WhenAll(older, newer);

        Assert.Equal("Run() : void", window.Title);
        Assert.DoesNotContain("class Alpha", window.Editor.Text);
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
    public async Task SelectNodeAsync_Should_DecompileInPlace_ForTheSelectedType()
    {
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "");
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);

        await window.SelectNodeAsync(Node(alpha));

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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "");
        window.Show();
        var alpha = ScriptDefinition("Foo.Bar.Alpha", memberName: null);

        await window.OpenDefinitionInNewTabAsync(CSharpDecompilerService.Decompile(alpha), alpha.AssemblyPath, alpha);

        Assert.Equal(1, window.TabCount);
        Assert.Equal("Alpha", Text((TreeViewItem)window.Tree.SelectedItem!));
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
    // The window was opened with no tree (assemblyPath: null): like ILSpy, F12 adds the target's
    // assembly to the tree, reveals the callee there and shows it on its own.
    public async Task NavigateAsync_Should_LoadTheTargetsAssembly_AndOpenTheCallee_WhenCaretIsOnACallSite()
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
