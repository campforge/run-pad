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
using Invoklet.Avalonia.CodeTextEditor;
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

namespace Invoklet.Runtime.UI.Tests;

// What the assembly explorer window does with what it is told about assemblies. The assembly is an
// in-memory ExplorerFixtureBrowser, so nothing here compiles or decompiles; reading real assemblies is
// tested in CSharpDecompilerServiceTests, and Invoklet.IntegrationTests keeps the flows through both.
[Trait("Category", "Decompiler")]
public class AssemblyExplorerWindowViewTests
{
    private static Mock<IThemeService> CreateThemeService()
    {
        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", ThemeName.DarkPlus, true)));
        return theme;
    }

    private static string FixturePath => ExplorerFixtureBrowser.Path;

    [AvaloniaFact]
    public async Task LoadAssemblyAsync_Should_BuildATree_GroupedByNamespace_OneNodePerType()
    {
        var windowService = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();

        await window.LoadAssemblyAsync(FixturePath);

        Assert.Equal(FixturePath, window.AssemblyPathText.Text);
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
        await window.LoadAssemblyAsync(FixturePath);
        var barNode = Namespace(window, "Foo.Bar");
        return barNode.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");
    }

    [AvaloniaFact]
    public async Task ExpandAsync_Should_ListBaseAndDerivedTypes_ThenMembers_WithIconsAndGreyedNonPublic()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);
        await window.ExpandAsync(alpha);
        var baseTypes = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Base Types");
        var objectItem = baseTypes.Items.OfType<TreeViewItem>().Single();

        await window.ExpandAsync(objectItem);

        Assert.Contains(objectItem.Items.OfType<TreeViewItem>(), i => Text(i)?.StartsWith("ToString()") == true);
    }

    // Opens Gamma.Call() (which calls Alpha.Run()) and then Alpha.Run(), as F12 on the call would.
    private async Task<(AssemblyExplorerWindow Window, TreeViewItem Call)> OpenCallAndNavigateToRunAsync()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();
        await window.LoadAssemblyAsync(FixturePath);
        var barNode = Namespace(window, "Foo.Bar");
        var gamma = barNode.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Gamma");
        await window.ExpandAsync(gamma);
        var call = gamma.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Call() : void");
        await window.SelectNodeAsync(Node(call), call);

        // What F12 on the call ends in: Alpha.Run() selected in the tree.
        var alpha = barNode.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");
        await window.ExpandAsync(alpha);
        var run = alpha.Items.OfType<TreeViewItem>().Single(n => Text(n) == "Run() : void");
        await window.SelectNodeAsync(Node(run), run);
        return (window, call);
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

    private static readonly DecompilerView IlView =
        new(DecompilerLanguage.IL, ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest);

    [AvaloniaFact]
    public async Task SetViewAsync_IL_Should_ReRenderTheCurrentPage_InPlace()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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

    // ── IL syntax highlighting of the code pane ──

    private static readonly ActiveTheme LightEditorTheme = new(
        new InterfaceTheme("Light", ThemeVariant.Light, false), new EditorTheme("Light+", ThemeName.LightPlus, false));

    private static int IndexOf(AssemblyExplorerWindow window, string text) =>
        window.Editor.Text.IndexOf(text, StringComparison.Ordinal);

    [AvaloniaFact]
    public async Task GoingBackToAPageRenderedAsCSharp_InTheIlView_Should_ShowItAsIlWithTheIlGrammar()
    {
        var (window, _) = await OpenCallAndNavigateToRunAsync(); // Call, then Run — both rendered as C#
        await window.SetViewAsync(IlView);
        Assert.Equal(IlGrammar.ScopeName, window.GrammarScope);

        window.GoBack();
        await TestWait.WhileAsync(() => !window.Editor.Text.Contains(".method"));

        Assert.Equal(IlGrammar.ScopeName, window.GrammarScope);
        Assert.Contains(".method", window.Editor.Text);
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
        var window = new AssemblyExplorerWindow((theme ?? CreateThemeService()).Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public async Task LoadAssemblyAsync_Should_BuildTheRowsInSlices_LetTheUiThreadRunBetweenThem_AndAttachOnce()
    {
        using var _ = new SmallSlices(2);
        var window = ShowWindow();
        var slices = 0;
        var inputRanBetweenSlices = false;
        var attachedMidBuild = false;
        window.TreeSliceBuilt = slice =>
        {
            slices = slice;
            attachedMidBuild |= window.Tree.Items.Count > 0;
            if (slice == 1) Dispatcher.UIThread.Post(() => inputRanBetweenSlices = true, DispatcherPriority.Input);
            if (slice == 2) Assert.True(inputRanBetweenSlices, "the dispatcher did not get a turn between two slices");
            return Task.CompletedTask;
        };

        var load = window.LoadAssemblyAsync(FixturePath);
        Assert.False(load.IsCompleted); // reading, grouping and the first slice are not done in this call
        var item = await load;

        Assert.True(slices >= 3, $"7+ types in slices of 2 need at least 3 slices, got {slices}");
        Assert.False(attachedMidBuild); // one coherent update: the row is attached when complete
        Assert.Same(item, Assert.Single(window.Tree.Items));
        Assert.Equal(["Foo.Bar", "Foo.Baz"], item!.Items.OfType<TreeViewItem>()
            .Where(i => Node(i).Kind == AssemblyNodeKind.Namespace && Text(i)!.StartsWith("Foo", StringComparison.Ordinal))
            .Select(Text));
        Assert.Equal(["Alpha", "Gamma"], Namespace(window, "Foo.Bar").Items.OfType<TreeViewItem>().Select(Text));
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_WhileTheTreeIsBuilding_Should_DropTheResult()
    {
        using var _ = new SmallSlices(1);
        var window = ShowWindow();
        window.TreeSliceBuilt = slice =>
        {
            if (slice == 1) window.Close();
            return Task.CompletedTask;
        };

        var item = await window.LoadAssemblyAsync(FixturePath);

        Assert.Null(item);
        Assert.Empty(window.Tree.Items);
        Assert.Empty(window.LoadedAssemblyPaths);
    }

    [AvaloniaFact]
    public async Task ConcurrentLoadsOfOnePath_Should_ShareOneBuild()
    {
        using var _ = new SmallSlices(2);
        var window = ShowWindow();
        var builds = 0;
        window.TreeSliceBuilt = slice =>
        {
            if (slice == 1) builds++;
            return Task.CompletedTask;
        };

        var first = window.LoadAssemblyAsync(FixturePath);
        var second = window.LoadAssemblyAsync(FixturePath);

        Assert.Same(await first, await second);
        Assert.Equal(1, builds);
        Assert.Single(window.Tree.Items);
    }

    private static string CopyOfExplorerAssembly() => ExplorerFixtureBrowser.SecondPath;

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

    [AvaloniaFact]
    public async Task ANewerOpenRequest_Should_KeepTheSelection_WhenAnOlderLoadFinishesLater()
    {
        using var _ = new SmallSlices(2);
        var second = CopyOfExplorerAssembly();
        var window = ShowWindow();

        await WithSlowLoadAsync(window, FixturePath,
            whileSlowLoads: () => window.OpenAssemblyAsync(second),
            afterRelease: () =>
            {
                Assert.Equal(2, window.Tree.Items.Count); // the older one is still added ...
                var selected = Assert.IsType<TreeViewItem>(window.Tree.SelectedItem);
                Assert.Equal(second, Node(selected).AssemblyPath); // ... but does not take the selection
                return Task.CompletedTask;
            });
    }

    [AvaloniaFact]
    public async Task ClickingARow_WhileAnAssemblyLoads_Should_KeepTheUsersSelection()
    {
        using var _ = new SmallSlices(2);
        var second = CopyOfExplorerAssembly();
        var window = ShowWindow();
        TreeViewItem? clicked = null;

        await WithSlowLoadAsync(window, FixturePath,
            whileSlowLoads: async () =>
            {
                var item = (await window.LoadAssemblyAsync(second))!;
                clicked = item.Items.OfType<TreeViewItem>().Single(i => Text(i) == "Foo.Bar").Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");
                window.Tree.SelectedItem = clicked;
            },
            afterRelease: () =>
            {
                Assert.Same(clicked, window.Tree.SelectedItem);
                Assert.Equal(2, window.Tree.Items.Count);
                return Task.CompletedTask;
            });
    }

    // A language switch while the first page of an empty tab is still rendering: the page lands in the
    // new view, labelled with it (Save Code offers .il; switching back re-renders it as C#).
    [AvaloniaFact]
    public async Task SwitchingTheView_WhileTheFirstPageRenders_Should_ShowItInTheNewView_AndSaveItAsIl()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-save-code-test").FullName;
        var path = Path.Combine(folder, "Alpha.il");
        var service = new Mock<IWindowService>();
        service.Setup(s => s.ShowSaveCodeFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, service.Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        try
        {
            window.Show();
            var alpha = await LoadAndFindAlphaAsync(window);

            var pending = window.SelectNodeAsync(Node(alpha), alpha);
            await window.SetViewAsync(IlView);
            await pending;

            Assert.Equal("Alpha", window.Title);
            Assert.Contains(".class", window.Editor.Text);
            Assert.False(window.CanGoBack);
            await window.SaveCodeAsync();
            service.Verify(s => s.ShowSaveCodeFileDialogAsync("Alpha", "il", It.IsAny<string>()), Times.Once);
            Assert.Contains(".class", await File.ReadAllTextAsync(path));

            await window.SetViewAsync(DecompilerView.Default);
            Assert.Contains("class Alpha", window.Editor.Text);
            Assert.DoesNotContain(".class", window.Editor.Text);
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    // The same after a page that can't be re-rendered (a References row's description).
    [AvaloniaFact]
    public async Task SwitchingTheView_WhileANewPageRenders_AfterAPageWithoutTarget_Should_ShowItInTheNewView()
    {
        var window = await OpenLoadedWindowAsync();
        try
        {
            var runtime = ReferencesRow(AssemblyRow(window)).Items.OfType<TreeViewItem>().Single(r => Text(r) == "System.Runtime");
            await window.SelectNodeAsync(Node(runtime), runtime);
            var alpha = Namespace(window, "Foo.Bar").Items.OfType<TreeViewItem>().Single(n => Text(n) == "Alpha");

            var pending = window.SelectNodeAsync(Node(alpha), alpha);
            await window.SetViewAsync(IlView);
            await pending;

            Assert.Equal("Alpha", window.Title);
            Assert.Contains(".class", window.Editor.Text);
            window.GoBack();
            Assert.StartsWith("// System.Runtime, Version=", window.Editor.Text);
        }
        finally { window.Close(); }
    }

    // After a page that can be re-rendered: the new page still wins — rather than the switch re-rendering
    // the page it's about to replace and dropping the selection — and the old one is re-rendered on Back.
    [AvaloniaFact]
    public async Task SwitchingTheView_WhileANewPageRenders_Should_ShowTheNewPage_NotReRenderTheOldOne()
    {
        var (window, alpha, run) = await OpenAlphaAndRunRowsAsync();
        try
        {
            await window.SelectNodeAsync(Node(run), run);

            var pending = window.SelectNodeAsync(Node(alpha), alpha);
            await window.SetViewAsync(IlView);
            await pending;

            Assert.Equal("Alpha", window.Title);
            Assert.Contains(".class", window.Editor.Text);
            window.GoBack();
            await TestWait.WhileAsync(() => !window.Editor.Text.Contains(".method"));
            Assert.Equal("Run() : void", window.Title);
        }
        finally { window.Close(); }
    }

    // A newer selection or a tab switch still supersedes the page rendering when the view changes.
    [AvaloniaFact]
    public async Task SwitchingTheView_WhileAPageRenders_Should_StillLetANewerSelectionOrATabSwitchWin()
    {
        var (window, alpha, run) = await OpenAlphaAndRunRowsAsync();
        try
        {
            var superseded = window.SelectNodeAsync(Node(alpha), alpha);
            await window.SetViewAsync(IlView);
            await window.SelectNodeAsync(Node(run), run);
            await superseded;

            Assert.Equal("Run() : void", window.Title);
            Assert.Contains(".method", window.Editor.Text);
            Assert.False(window.CanGoBack); // Alpha never landed

            await window.OpenInNewTabAsync(Node(alpha), alpha);
            window.ActivateNextTab(-1); // back to Run's tab
            var left = window.SelectNodeAsync(Node(alpha), alpha);
            await window.SetViewAsync(DecompilerView.Default);
            window.ActivateNextTab(+1); // Alpha's tab, re-rendered as C#
            await left;
            await TestWait.WhileAsync(() => !window.Editor.Text.Contains("class Alpha"));

            Assert.Equal(["Run() : void", "Alpha"], window.TabTitles); // the left tab's selection didn't land
            Assert.Equal("Alpha", window.Title);
            window.ActivateNextTab(-1);
            await TestWait.WhileAsync(() => !window.Editor.Text.Contains("void Run()"));
            Assert.False(window.CanGoBack);
        }
        finally { window.Close(); }
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();
        await window.LoadAssemblyAsync(FixturePath);
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
        await window.OpeningResult.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Gamma", window.Title);
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

    // The macOS defaults: Cmd instead of Ctrl.
    [AvaloniaFact]
    public void OnMacOS_CmdShiftF_Should_FocusTheSearchBox()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "")
        {
            Preferences = WindowsLinuxKeys(mac: true),
        };
        window.Show();
        var searchBox = window.FindControl<TextBox>("SearchBox")!;

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(searchBox.IsFocused);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Meta | RawInputModifiers.Shift);
        Assert.True(searchBox.IsFocused);
    }

    // Rebinding in Preferences while the explorer is open: its own keys, its editor's keys and the
    // key hints follow at once; the old key stops working.
    [AvaloniaFact]
    public void RebindingKeys_WhileTheWindowIsOpen_Should_TakeEffectAtOnce()
    {
        var keyboard = new KeyboardSettings { IsMac = false };
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Keyboard).Returns(keyboard);
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "")
        {
            Preferences = prefs.Object,
        };
        window.Show();
        var searchBox = window.FindControl<TextBox>("SearchBox")!;
        Assert.Equal(new KeyGesture(Key.F12), window.Editor.GoToDefinitionGesture);

        keyboard.SetKey(KeyCommands.ExplorerSearch, "Ctrl+Alt+F");
        keyboard.SetKey(KeyCommands.GoToDefinition, "Ctrl+F12");
        prefs.Raise(p => p.Changed += null);

        Assert.Equal(new KeyGesture(Key.F12, KeyModifiers.Control), window.Editor.GoToDefinitionGesture);
        Assert.Contains("(Ctrl+Alt+F)", searchBox.PlaceholderText);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(searchBox.IsFocused);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.True(searchBox.IsFocused);

        // closed: no longer follows Preferences
        window.Close();
        keyboard.SetKey(KeyCommands.GoToDefinition, null);
        prefs.Raise(p => p.Changed += null);
        Assert.Equal(new KeyGesture(Key.F12, KeyModifiers.Control), window.Editor.GoToDefinitionGesture);
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

        var again = await window.LoadAssemblyAsync(FixturePath);

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
        // the open itself, however long a loaded machine takes to decompile, not a guess from the title
        await window.OpeningResult.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Call() : void", window.Title);
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
            new DecompiledMemberResult("// code", 1, title), assemblyPath: null)
        { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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

    // ── Drag & drop, Ctrl+hover ──

    [AvaloniaFact]
    public async Task OpenDroppedFilesAsync_Should_AddTheAssemblies_AndIgnoreOtherFiles()
    {
        var service = new Mock<IWindowService>();
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, service.Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();

        await window.OpenDroppedFilesAsync([FixturePath, Path.ChangeExtension(FixturePath, ".txt")]);

        Assert.Equal([FixturePath], window.LoadedAssemblyPaths);
        Assert.Same(AssemblyRow(window), window.Tree.SelectedItem);
        service.Verify(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, service.Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();
        Assert.False(window.FindControl<Button>("SaveCodeButton")!.IsEnabled);

        var alpha = await LoadAndFindAlphaAsync(window);
        await window.SelectNodeAsync(Node(alpha), alpha);

        Assert.True(window.FindControl<Button>("SaveCodeButton")!.IsEnabled);
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
    public async Task SaveCodeAsync_InIlView_Should_WriteIl_ToAnIlFile()
    {
        var folder = Directory.CreateTempSubdirectory("Invoklet-save-code-test").FullName;
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
        var folder = Directory.CreateTempSubdirectory("Invoklet-save-code-test").FullName;
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();
        await window.LoadAssemblyAsync(FixturePath);
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
        var theme = new InvokletTheme();
        var variants = typeof(InvokletThemeVariants).GetFields()
            .Select(f => (ThemeVariant)f.GetValue(null)!)
            .ToList();
        Assert.Equal(8, variants.Count);

        foreach (var variant in variants)
        {
            Assert.True(theme.TryGetResource("Invoklet.Panel.BackgroundBrush", variant, out var brush), variant.ToString());
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
        window.Show();
        var alpha = await LoadAndFindAlphaAsync(window);

        window.Tree.SelectedItem = alpha;

        await TestWait.WhileAsync(() => window.Title != "Alpha");
        Assert.Contains("class Alpha", window.Editor.Text);
    }

    [AvaloniaFact]
    public async Task SelectNodeAsync_Should_KeepTheNewestSelection_WhenAnOlderOneFinishesLater()
    {
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, new Mock<IWindowService>().Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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
            Directory.CreateTempSubdirectory("Invoklet-assembly-explorer-test").FullName, "notadll.dll");
        await File.WriteAllTextAsync(notAnAssembly, "not a real assembly");
        try
        {
            var windowService = new Mock<IWindowService>();
            windowService.Setup(s => s.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
            var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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
        var window = new AssemblyExplorerWindow(CreateThemeService().Object, windowService.Object, "") { Preferences = WindowsLinuxKeys(), Browser = new ExplorerFixtureBrowser() };
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

    // F12 from the user's script: the definition carries names only (DecompiledReference stands in for
    // the IntelliSense result; its tokens aren't used by this entry point).
    private AssemblyExplorerWindow OpenFromScriptF12(string typeName, string? memberName, string display)
    {
        var definition = new DecompiledReference(
            FixturePath, typeName, memberName, [], display, MetadataToken: 0, ContainingTypeMetadataToken: 0);
        var initial = CSharpDecompilerService.Decompile(definition);
        var window = new AssemblyExplorerWindow(
            CreateThemeService().Object, new Mock<IWindowService>().Object, initial, FixturePath, definition);
        window.Show();
        return window;
    }

    private DecompiledReference ScriptDefinition(string typeName, string? memberName) => new(
        FixturePath, typeName, memberName, [], memberName is null ? typeName : $"{typeName}.{memberName}",
        MetadataToken: 0, ContainingTypeMetadataToken: 0);

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
}
