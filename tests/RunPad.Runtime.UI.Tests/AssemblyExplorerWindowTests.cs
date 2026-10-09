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
        var barNode = namespaceNodes.Single(n => Text(n) == "Foo.Bar");
        Assert.Equal(["Alpha", "Gamma"], barNode.Items.OfType<TreeViewItem>().Select(Text));
        Assert.Equal(AssemblyNodeKind.Namespace, Node(barNode).Kind);
        Assert.NotNull(Node(barNode).Icon);
    }

    private static AssemblyTreeNode Node(TreeViewItem item) => Assert.IsType<AssemblyTreeNode>(item.Header);

    private static string? Text(TreeViewItem item) => (item.Header as AssemblyTreeNode)?.Text;

    private async Task<TreeViewItem> LoadAndFindAlphaAsync(AssemblyExplorerWindow window)
    {
        await window.LoadAssemblyAsync(_explorerAssembly.BinaryPath);
        var barNode = window.Tree.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Foo.Bar");
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
        var barNode = window.Tree.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Foo.Bar");
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
        var gamma = window.Tree.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Foo.Bar")
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
        var alpha = window.Tree.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Foo.Bar")
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
        var alpha = window.Tree.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Foo.Bar")
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
        var bazNode = window.Tree.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Foo.Baz");
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
        var namespaceNodes = window.Tree.Items.OfType<TreeViewItem>().ToList();
        Assert.Contains(namespaceNodes, n => Text(n) == "Foo.Baz");
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
