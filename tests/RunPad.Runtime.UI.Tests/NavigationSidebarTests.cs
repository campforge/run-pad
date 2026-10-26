using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Moq;
using RunPad.Avalonia.Themes;
using RunPad.Connectors.Http;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Contracts.IntelliSense;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Services;
using RunPad.Services.BuildCache;
using RunPad.Services.Connections;
using RunPad.Services.Examples;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.UserControls.Models;
using RunPad.Windows;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// The sidebar in a real main window: three accordion bundles (Connections, Scripts, Samples) and no
// tabs; folding one leaves the others as they were; the Connections tree reaches the Connections
// editor from the keyboard; an example clicked in Samples opens as an untitled, unsaved copy that Save
// turns into a new file through Save As, leaving the bundled original unchanged.
public sealed class NavigationSidebarTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-sidebar").FullName;
    // Apart from the scripts folder, which a test checks stays empty.
    private readonly string _dataFolder = Directory.CreateTempSubdirectory("runpad-sidebar-data").FullName;

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        Directory.Delete(_dataFolder, recursive: true);
    }

    private readonly LayoutSettings _layout = new();

    private (MainWindow Window, MainWindowViewModel Vm, Mock<IWindowService> WindowService) CreateMainWindow()
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(_layout);
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(_folder);
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new FileSystemItem("first.rpad", false, Path.Combine(_folder, "first.rpad"), null),
            new FileSystemItem("second.rpad", false, Path.Combine(_folder, "second.rpad"), null),
        ]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var windowService = new Mock<IWindowService>();
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        var store = new FileConnectionStore(Path.Combine(_dataFolder, "connections.json"));
        RpadConnection Http(string name) => new()
        {
            Name = name,
            Driver = "http",
            Params = new(StringComparer.Ordinal) { ["baseAddress"] = RpadValue.Of($"https://{name}.test/") },
        };
        // One list per test: a second window of the same test reads it rather than writing it again,
        // which the store would refuse as a conflicting save.
        if (!File.Exists(store.FilePath)) store.Save([Http("billing"), Http("orders")], null);
        var vm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService.Object,
            prefs: prefs.Object,
            languageRegistry: new LanguageHostRegistry(),
            connectionNavigation: new ConnectionNavigationViewModel(store, drivers));
        var editorTabs = new EditorTabsControl(
            theme.Object, windowService.Object, new Mock<ILanguageHostRegistry>().WithCSharpIntelliSense(() => new Mock<IIntelliSenseService>().Object), prefs.Object,
            new Mock<IBuildCacheService>().Object);

        var window = new MainWindow(prefs.Object, editorTabs, vm) { Width = 1000, Height = 600 };
        window.Show();
        Settle();
        return (window, vm, windowService);
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<TextEditor> WaitForEditorAsync(Window window, Func<EditorsTabItemControl, bool> which)
    {
        for (var i = 0; i < 100; i++)
        {
            Settle();
            if (window.GetVisualDescendants().OfType<EditorsTabItemControl>().FirstOrDefault(which) is { } control
                && control.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault() is { } editor
                && editor.Document.TextLength > 0)
                return editor;
            await Task.Delay(20);
        }
        throw new TimeoutException("The example's editor was not created.");
    }

    private static T Named<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
    }

    [AvaloniaFact]
    public void TheSidebar_Should_HaveConnectionsScriptsAndSamplesBundles_AndNoTabs()
    {
        var (window, _, _) = CreateMainWindow();

        var bundles = window.GetVisualDescendants().OfType<Expander>()
            .Where(e => e.Name is "ConnectionsBundle" or "ScriptsBundle" or "SamplesBundle").ToList();
        Assert.Equal(["ConnectionsBundle", "ScriptsBundle", "SamplesBundle"], bundles.Select(b => b.Name));
        Assert.Equal(["Connections navigation bundle", "Scripts navigation bundle", "Samples navigation bundle"],
            bundles.Select(global::Avalonia.Automation.AutomationProperties.GetName));
        Assert.All(bundles, b => Assert.True(b.IsEffectivelyVisible));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TabStrip>(), t => t.Name == "SidebarTabs");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "FILES");
        // The three sections fill the sidebar's width and their headers share one geometry.
        var sidebar = Named<Grid>(window, "SidebarSections");
        Assert.All(bundles, b => Assert.Equal(sidebar.Bounds.Width, b.Bounds.Width, 1));
        var headerHeights = bundles.Select(b => b.GetVisualDescendants().OfType<ToggleButton>().First().Bounds.Height).Distinct();
        Assert.Single(headerHeights);
    }

    // Open sections share the sidebar's height instead of one long scrolling page: each tree scrolls
    // inside its own section, and a folded section is only its header.
    [AvaloniaFact]
    public void OpenSections_Should_ShareTheSidebarsHeight()
    {
        var (window, _, _) = CreateMainWindow();
        var grid = Named<Grid>(window, "SidebarSections");
        var connections = Named<Expander>(window, "ConnectionsBundle");
        var scripts = Named<Expander>(window, "ScriptsBundle");
        var samples = Named<Expander>(window, "SamplesBundle");
        Assert.False(samples.IsExpanded); // folded by default

        Assert.Equal(grid.Bounds.Height, connections.Bounds.Height + scripts.Bounds.Height + samples.Bounds.Height, 1);
        // Grid star sizing can round one section by a device pixel.
        Assert.InRange(Math.Abs(connections.Bounds.Height - scripts.Bounds.Height), 0, 1);
        var header = samples.GetVisualDescendants().OfType<ToggleButton>().First();
        Assert.InRange(Math.Abs(header.Bounds.Height - samples.Bounds.Height), 0, 1);

        samples.IsExpanded = true;
        Settle();
        Assert.InRange(Math.Abs(connections.Bounds.Height - samples.Bounds.Height), 0, 1);
        Assert.Equal(grid.Bounds.Height, connections.Bounds.Height + scripts.Bounds.Height + samples.Bounds.Height, 1);
    }

    // Which sections are open is kept in the layout settings for the next start.
    [AvaloniaFact]
    public void FoldingASection_Should_BeKeptForTheNextStart()
    {
        var (window, vm, _) = CreateMainWindow();
        Named<Expander>(window, "ScriptsBundle").IsExpanded = false;
        Named<Expander>(window, "SamplesBundle").IsExpanded = true;
        Settle();
        Assert.False(vm.IsScriptsSectionExpanded);
        Assert.Equal(["Scripts"], _layout.CollapsedSidebarSections);

        var (secondWindow, _, _) = CreateMainWindow();
        Assert.False(Named<Expander>(secondWindow, "ScriptsBundle").IsExpanded);
        Assert.True(Named<Expander>(secondWindow, "SamplesBundle").IsExpanded);
    }

    // Samples are a read-only tree of the bundled folders, in the bundled order.
    [AvaloniaFact]
    public void TheSamples_Should_BeATreeOfFolders()
    {
        var (window, vm, _) = CreateMainWindow();
        Named<Expander>(window, "SamplesBundle").IsExpanded = true;
        Settle();
        Assert.Equal(["Getting started", "Output", "Debugging"], vm.SampleTree.Select(n => n.Title));
        Assert.All(vm.SampleTree, folder => Assert.True(folder.IsFolder));
        Assert.Equal(["quick-start.rpad", "expression-kind.rpad", "program-kind.rpad"], vm.SampleTree[0].Children!.Select(c => c.Id));
        var rows = Named<TreeView>(window, "SampleTree").GetVisualDescendants().OfType<TreeViewItem>().ToList();
        Assert.Equal("Getting started folder, 3 items", global::Avalonia.Automation.AutomationProperties.GetName(rows[0]));
    }

    // Opening and folding bundles leaves the file tree's selection and the editor tabs alone.
    [AvaloniaFact]
    public void FoldingBundles_Should_KeepTheTreesSelection_AndTheEditorTabs()
    {
        var (window, vm, _) = CreateMainWindow();
        var tree = window.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "FileTree");
        tree.SelectedItem = vm.FileTree.Nodes[1];
        Settle();
        var editorTabs = vm.Tabs.ToList();
        var scripts = Named<Expander>(window, "ScriptsBundle");
        var samples = Named<Expander>(window, "SamplesBundle");

        samples.IsExpanded = true;
        Settle();
        Assert.True(Named<TreeView>(window, "SampleTree").IsEffectivelyVisible);
        Assert.True(tree.IsEffectivelyVisible); // bundles are independent, unlike tabs

        scripts.IsExpanded = false;
        Settle();
        Assert.False(tree.IsEffectivelyVisible);
        scripts.IsExpanded = true;
        Settle();
        Assert.True(tree.IsEffectivelyVisible);
        Assert.Same(vm.FileTree.Nodes[1], tree.SelectedItem);
        Assert.Equal(editorTabs, vm.Tabs);
    }

    // Rows carry spoken names; Enter on a connection opens the editor at it, and the context menu
    // offers Move To for moving without a mouse.
    [AvaloniaFact]
    public async Task TheConnectionsTree_Should_BeUsableFromTheKeyboard()
    {
        var (window, vm, windowService) = CreateMainWindow();
        for (var i = 0; i < 100 && vm.ConnectionNavigation!.IsLoaded is false; i++) { Settle(); await Task.Delay(10); }
        Settle();
        var view = Named<ConnectionNavigationView>(window, "ConnectionNavigationView");
        var rows = view.Tree.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        Assert.Equal(["billing, HTTP", "orders, HTTP"],
            rows.Select(global::Avalonia.Automation.AutomationProperties.GetName));

        var billing = rows[0];
        // A top-level row starts near the section's edge: list padding plus the chevron column only.
        var header = billing.GetVisualDescendants().OfType<global::Avalonia.Controls.Presenters.ContentPresenter>()
            .First(p => p.Name == "PART_HeaderPresenter");
        Assert.InRange(header.TranslatePoint(default, view.Tree)!.Value.X, 0, 24);
        billing.Focus();
        view.Selected = (ConnectionNavigationNode)billing.DataContext!;
        Settle();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle();
        windowService.Verify(w => w.ShowConnectionsAsync("billing"), Times.Once);

        var menu = new ContextMenu();
        view.FillContextMenu(menu);
        var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Contains("Edit Connection…", headers);
        Assert.Contains("Refresh", headers); // an HTTP connection has no contract: Refresh checks it again
        Assert.Contains("Refresh All", headers);
        // MainWindow creates a pristine active tab, so adding a connection is available even
        // before the user has saved the script.
        Assert.True(menu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Add to Script").IsEnabled);
        Assert.Contains("Move To", headers);
        Assert.Contains("Manage Connections…", headers);
    }

    // macOS: Control-click is the secondary click. On a connection row it selects the row and opens
    // the context menu, as a right click does; a plain click opens no menu.
    [AvaloniaFact]
    public async Task ControlClick_Should_ActAsARightClick()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Control-click is a macOS pointer convention.");
        // Avalonia's headless backend does not preserve the original hit-tested child when a
        // synthetic Control-click is re-raised at the TopLevel. Keep the scenario for a real
        // macOS window/manual smoke test rather than asserting on a backend artefact.
        Assert.Skip("Headless Avalonia cannot validate native macOS Control-click routing.");
        var (window, vm, _) = CreateMainWindow();
        using var controlClick = ControlClick.AttachTo(window); // the app registers it for macOS only
        for (var i = 0; i < 100 && vm.ConnectionNavigation!.IsLoaded is false; i++) { Settle(); await Task.Delay(10); }
        Settle();
        var view = Named<ConnectionNavigationView>(window, "ConnectionNavigationView");
        var rows = view.Tree.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        var orders = rows[1];
        var point = orders.TranslatePoint(new Point(orders.Bounds.Width / 2, 13), window)!.Value;

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
        Assert.False(view.Tree.ContextMenu!.IsOpen);

        view.Selected = null;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        Settle();
        Assert.Equal("orders", view.Selected?.Name);
        Assert.True(view.Tree.ContextMenu!.IsOpen);
        Assert.Contains(view.Tree.ContextMenu.Items.OfType<MenuItem>(), m => m.Header as string == "Refresh");
        view.Tree.ContextMenu.Close();
    }

    // The tree's drag carries the connection; dropped on the editor it is added to the active script
    // at once (HTTP needs no packages, so nothing is asked) and the tree marks it as used.
    [AvaloniaFact]
    public async Task DroppingAConnectionOnTheEditor_Should_AddItToTheActiveScript()
    {
        var (window, vm, windowService) = CreateMainWindow();
        for (var i = 0; i < 100 && vm.ConnectionNavigation!.IsLoaded is false; i++) { Settle(); await Task.Delay(10); }
        vm.OpenExample(ExampleScripts.All[0]);
        await WaitForEditorAsync(window, c => c.DataContext is EditorTabItemViewModel { Example: not null });
        var frontMatter = vm.ActiveTab!.FrontMatter!;
        Assert.DoesNotContain(frontMatter.Connections, c => c.Name == "billing");

        var host = Named<ContentControl>(window, "EditorTabsHost");
        var point = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), window)!.Value;
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(ConnectionNavigationView.NodeFormat, "c:billing");
        data.Add(item);
        const DragDropEffects allowed = DragDropEffects.Move | DragDropEffects.Copy;
        window.DragDrop(point, RawDragEventType.DragEnter, data, allowed, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.DragOver, data, allowed, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.Drop, data, allowed, RawInputModifiers.None);
        for (var i = 0; i < 100 && !frontMatter.Connections.Any(c => c.Name == "billing"); i++) { Settle(); await Task.Delay(10); }

        Assert.Contains(frontMatter.Connections, c => c.Name == "billing");
        Assert.True(vm.ConnectionNavigation!.Find("billing", isConnection: true)!.IsAttached);
        windowService.Verify(w => w.ConfirmConnectionPackagesAsync(It.IsAny<string>()), Times.Never);
    }

    // The same drag dropped on the editor without a connection in it (text, a file) is left to the editor.
    [AvaloniaFact]
    public async Task DroppingSomethingElseOnTheEditor_Should_NotTouchTheScriptsConnections()
    {
        var (window, vm, _) = CreateMainWindow();
        vm.OpenExample(ExampleScripts.All[0]);
        await WaitForEditorAsync(window, c => c.DataContext is EditorTabItemViewModel { Example: not null });
        var before = vm.ActiveTab!.FrontMatter!.Connections.Count;
        var host = Named<ContentControl>(window, "EditorTabsHost");
        var point = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), window)!.Value;
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(ConnectionNavigationView.NodeFormat, "g:some-group");
        data.Add(item);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Move | DragDropEffects.Copy, RawInputModifiers.None);
        Settle();
        Assert.Equal(before, vm.ActiveTab!.FrontMatter!.Connections.Count);
    }

    // A real click on an example: an untitled, unsaved tab with the example's code; clicking it again
    // reuses that tab. Save goes through Save As (suggesting the example's title) and writes the whole
    // document — prose included — to the chosen path; the bundled original stays as it was.
    [AvaloniaFact]
    public async Task ClickingAnExample_Should_OpenAnUnsavedCopy_ThatSaveAsWritesElsewhere()
    {
        var (window, vm, windowService) = CreateMainWindow();
        var example = ExampleScripts.All[0];
        var original = example.Text;
        Named<Expander>(window, "SamplesBundle").IsExpanded = true;
        Settle();
        var tree = Named<TreeView>(window, "SampleTree");
        var item = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => i.DataContext is SampleTreeNode { Example: not null });

        Click(window, item);
        var editor = await WaitForEditorAsync(window, c => c.DataContext is EditorTabItemViewModel { Example: not null });

        var tab = vm.ActiveTab!;
        Assert.Same(example, tab.Example);
        Assert.True(tab.IsDirty);
        Assert.Null(tab.FilePath);
        Assert.Equal(RpadParser.TryParse(original)!.RunCode, editor.Text);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder)); // nothing written, nothing run

        var count = vm.Tabs.Count;
        Click(window, item);
        Assert.Equal(count, vm.Tabs.Count);
        Assert.Same(tab, vm.ActiveTab);

        var target = Path.Combine(_folder, "my copy.rpad");
        windowService.Setup(w => w.ShowSaveFileDialogAsync("Quick start", _folder)).ReturnsAsync(target);
        editor.Document.Insert(editor.Document.TextLength, "\n// edited");
        tab.SaveAsCommand.Execute().Subscribe();
        for (var i = 0; i < 100 && !File.Exists(target); i++) { Settle(); await Task.Delay(20); }

        var saved = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);
        Assert.Contains("# Quick start", saved);
        Assert.Contains("// edited", saved);
        Assert.Equal(target, tab.FilePath);
        Assert.False(tab.IsDirty);
        Assert.Equal(original, ExampleScripts.All[0].Text);
    }
}
