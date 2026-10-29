using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Invoklet.Avalonia.Themes;
using Invoklet.Contracts;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.VB;
using Invoklet.Services;
using Invoklet.Services.BuildCache;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.Windows;
using Invoklet.Windows.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// WindowDecorations.Full (the Linux fallback, since a CSD request comes back with no
// border/shadow/corner-radius on a real KDE/KWin/Wayland session) puts the OS's own native titlebar
// above our custom title row, which still drew its own copy of the app icon — a visible duplicate
// ("extra icon" from the original TODO note) since the native titlebar already shows Window.Icon.
public class MainWindowChromeTests
{
    private static MainWindow CreateMainWindow(IWindowService? windowService = null)
        => CreateMainWindow(new KeyboardSettings(), new Mock<IPreferencesService>(), windowService);

    // The languages the app registers, in its order.
    private static LanguageHostRegistry Languages(params ILanguageHostFactory[] factories)
    {
        var registry = new LanguageHostRegistry();
        foreach (var factory in factories) registry.Register(factory);
        return registry;
    }

    private static MainWindow CreateMainWindow(
        KeyboardSettings keyboard, Mock<IPreferencesService> prefs, IWindowService? windowService = null)
    {
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        prefs.Setup(p => p.Keyboard).Returns(keyboard);
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());

        var fileSystem = new Mock<IFileSystemService>();
        fileSystem.Setup(f => f.Folder).Returns(Path.GetTempPath());
        fileSystem.Setup(f => f.GetFilesTreeAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var theme = new Mock<IThemeService>();
        theme.Setup(t => t.CurrentTheme).Returns(new ActiveTheme(
            new InterfaceTheme("Test", ThemeVariant.Dark, true),
            new EditorTheme("Test", TextMateSharp.Grammars.ThemeName.DarkPlus, true)));

        var registry = new Mock<ILanguageHostRegistry>();
        var buildCache = new Mock<IBuildCacheService>();
        var intelliSense = new Mock<IIntelliSenseService>();

        var mainVm = new MainWindowViewModel(
            sdkProviders: [],
            fileSystemService: fileSystem.Object,
            windowService: windowService ?? new Mock<IWindowService>().Object,
            prefs: prefs.Object,
            languageRegistry: Languages(new CSharpLanguageHostFactory(NullLoggerFactory.Instance),
                new VbLanguageHostFactory(NullLoggerFactory.Instance)));

        var editorTabs = new EditorTabsControl(
            theme.Object, new Mock<IWindowService>().Object, registry.WithCSharpIntelliSense(() => intelliSense.Object), prefs.Object,
            buildCache.Object);

        return new MainWindow(prefs.Object, editorTabs, mainVm);
    }

    [AvaloniaFact]
    public void ApplyPlatformChrome_Should_HideNativeTitlebarDuplicates_OnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var window = CreateMainWindow();

        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        Assert.False(window.ExtendClientAreaToDecorationsHint);

        var windowControlButtons = window.FindControl<StackPanel>("WindowControlButtons");
        Assert.NotNull(windowControlButtons);
        Assert.False(windowControlButtons!.IsVisible);

        var appIcon = window.FindControl<Image>("AppIconImage");
        Assert.NotNull(appIcon);
        Assert.False(appIcon!.IsVisible);
    }

    // Native titlebar stays separate from the Rider-style application toolbar.
    [AvaloniaFact]
    public void ApplyPlatformChrome_Should_KeepNativeTitlebarAndToolbar_OnMacOS()
    {
        if (!OperatingSystem.IsMacOS()) return;

        using var window = CreateMainWindow();

        Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.True(window.CanResize);
        Assert.True(window.FindControl<Border>("TitleBar")!.IsVisible);
        Assert.False(window.FindControl<StackPanel>("WindowControlButtons")!.IsVisible);
        Assert.True(window.FindControl<MacWindowButtons>("MacWindowButtons")!.IsVisible);
        Assert.False(window.FindControl<Image>("AppIconImage")!.IsVisible);
        Assert.Equal(0, window.FindControl<StackPanel>("TitleBarActions")!.Margin.Left);
        Assert.False(window.FindControl<Menu>("MainMenu")!.IsVisible);
        Assert.NotNull(NativeMenu.GetMenu(window));
        Assert.Equal(32, window.FindControl<Grid>("RootLayout")!.RowDefinitions[0].Height.Value);
        Assert.Equal(30, window.FindControl<Button>("SidebarToggleButton")!.Width);
        Assert.Equal(14, window.FindControl<PathIcon>("SidebarToggleIcon")!.Width);
    }

    // The same application menu paths on every OS, File → Exit included (macOS has no Quit of its
    // own in the application menu). Runs on any OS: both projections are built from
    // MainWindow.MainMenuEntries regardless of platform.
    private static readonly string[] ExpectedMenuTree =
    [
        "File",
        "File/New File",
        "File/New RPad File",
        "File/New RPad File/C#",
        "File/New RPad File/Visual Basic",
        "File/Open...",
        "File/Recent Files",
        "File/Recent Files/Clear Recent Files",
        "File/Open Text File...",
        "File/Import .linq Query...",
        "File/---",
        "File/Save",
        "File/Save As...",
        "File/Revert to Saved",
        "File/---",
        "File/Close Tab",
        "File/---",
        "File/Exit",
        "Edit",
        "Edit/Undo",
        "Edit/Redo",
        "Edit/---",
        "Edit/Cut",
        "Edit/Copy",
        "Edit/Paste",
        "Edit/Select All",
        "Edit/---",
        "Edit/Format Document",
        "Edit/Go to Line...",
        "Edit/Resolve All Usings",
        "View",
        "View/Toggle File Tree",
        "View/Toggle Output Panel",
        "View/---",
        "View/Markdown Preview",
        "View/Markdown Preview to the Side",
        "View/---",
        "View/Zoom In",
        "View/Zoom Out",
        "View/Reset Zoom",
        "Run",
        "Run/Run / Continue",
        "Run/Stop",
        "Run/Execute Selection",
        "Run/Stop and Run Again",
        "Run/---",
        "Run/Debug Mode",
        "Run/---",
        "Run/Step Over",
        "Run/Step Into",
        "Run/Step Out",
        "Run/Pause",
        "Run/Run to Cursor",
        "Tools",
        "Tools/Dependencies...",
        "Tools/Assembly Explorer...",
        "Tools/Connections...",
        "Tools/Secret Store...",
        "Tools/---",
        "Tools/Preferences...",
        "Help",
        "Help/User Guide",
        "Help/Release Notes",
        "Help/Report an Issue",
        "Help/---",
        "Help/About Invoklet",
    ];

    [AvaloniaFact]
    public void MainMenu_Should_ProjectIdenticalTrees_ForInWindowAndNativeMenus()
    {
        using var window = CreateMainWindow();

        var inWindow = Describe(window.FindControl<Menu>("MainMenu")!.Items, "").ToList();
        var native = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "").ToList();

        Assert.Equal(ExpectedMenuTree, inWindow.Select(n => n.Path));
        Assert.Equal(ExpectedMenuTree, native.Select(n => n.Path));
        // Same ICommand instance behind each path → identical routing, not just identical labels;
        // and the same key shown next to it.
        Assert.Equal(inWindow.Select(n => n.Command), native.Select(n => n.Command));
        Assert.Equal(inWindow.Select(n => n.Gesture), native.Select(n => n.Gesture));
        Assert.All(inWindow.Where(n => IsLeaf(n.Path)), n => Assert.NotNull(n.Command));

        // Only macOS exports a window NativeMenu (system menu bar); elsewhere it would duplicate the
        // in-window Menu (e.g. a Linux DBus global menu).
        var exported = NativeMenu.GetMenu(window);
        if (OperatingSystem.IsMacOS())
        {
            Assert.NotNull(exported);
            var exportedNodes = Describe(exported!, "").ToList();
            Assert.Equal(ExpectedMenuTree, exportedNodes.Select(n => n.Path));
            Assert.Equal(inWindow.Select(n => n.Command), exportedNodes.Select(n => n.Command));
            Assert.Equal(inWindow.Select(n => n.Gesture), exportedNodes.Select(n => n.Gesture));
        }
        else
        {
            Assert.Null(exported);
        }
    }

    [AvaloniaFact]
    public void MainMenu_Should_RouteToRealActions()
    {
        var windowService = new Mock<IWindowService>();
        using var window = CreateMainWindow(windowService.Object);
        var vm = (MainWindowViewModel)window.DataContext!;
        var commands = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "")
            .Where(n => n.Command is not null)
            .ToDictionary(n => n.Path, n => n.Command!);

        var tabCountBefore = vm.Tabs.Count;
        commands["File/New File"].Execute(null);
        Assert.Equal(tabCountBefore + 1, vm.Tabs.Count);

        var sizeBefore = vm.Sidebar.Size;
        commands["View/Toggle File Tree"].Execute(null);
        Assert.NotEqual(sizeBefore, vm.Sidebar.Size);

        commands["Tools/Preferences..."].Execute(null);
        commands["Help/About Invoklet"].Execute(null);
        windowService.Verify(w => w.ShowPreferencesAsync(), Times.Once);
        windowService.Verify(w => w.ShowAboutAsync(), Times.Once);
    }

    // Run, View and Edit items act on the active tab — the same code as its toolbar and shortcuts.
    [AvaloniaFact]
    public async Task MainMenu_Should_RouteToTheActiveTab()
    {
        using var window = CreateMainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        var commands = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "")
            .Where(n => n.Command is not null)
            .ToDictionary(n => n.Path, n => n.Command!);

        var editor = await WaitForActiveEditorAsync(window);
        var tab = vm.ActiveTab!;

        var debugMode = tab.DebugModeEnabled;
        commands["Run/Debug Mode"].Execute(null);
        Assert.NotEqual(debugMode, tab.DebugModeEnabled);

        var outputSize = tab.OutputPanel.Size;
        commands["View/Toggle Output Panel"].Execute(null);
        Assert.NotEqual(outputSize, tab.OutputPanel.Size);

        // Reset Zoom returns to the font size set in Preferences (100%), whatever the zoom was.
        var baseFontSize = new EditorSettings().FontSize;
        commands["View/Reset Zoom"].Execute(null);
        Assert.Equal(baseFontSize, editor.FontSize);
        commands["View/Zoom In"].Execute(null);
        Assert.True(editor.FontSize > baseFontSize);
        commands["View/Reset Zoom"].Execute(null);
        Assert.Equal(baseFontSize, editor.FontSize);

        editor.Text = "var answer = 42;";
        Assert.True(editor.TextArea.Focus());
        commands["Edit/Select All"].Execute(null);
        Assert.Equal(editor.Text.Length, editor.SelectionLength);
    }

    // Opening the in-window menu can take focus away from the editor; the Edit menu still acts on
    // the text control that had focus last.
    [AvaloniaFact]
    public async Task EditMenu_Should_ActOnTheLastFocusedTextControl()
    {
        using var window = CreateMainWindow();
        window.Show();
        var commands = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "")
            .Where(n => n.Command is not null)
            .ToDictionary(n => n.Path, n => n.Command!);
        var editor = await WaitForActiveEditorAsync(window);
        editor.Text = "abc";
        Assert.True(editor.TextArea.Focus());

        // Any non-text control taking focus, as a menu item does when the menu opens.
        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Focusable);
        Assert.True(button.Focus());
        Assert.Same(button, window.FocusManager!.GetFocusedElement());
        Assert.Same(editor, window.EditTarget);

        commands["Edit/Select All"].Execute(null);
        Assert.Equal(3, editor.SelectionLength);
    }

    // The application menu must already exist when App.Initialize returns — Avalonia.Native's
    // exporter is created right after (AfterSetup) and otherwise installs "About Avalonia". It is
    // empty: About is under Help and Exit under File, as on every other OS.
    [AvaloniaFact]
    public void AppInitialize_Should_SetAnEmptyApplicationMenu()
    {
        var app = new App();
        app.Initialize();

        var menu = NativeMenu.GetMenu(app);
        Assert.NotNull(menu);
        Assert.Empty(menu!.Items);
    }

    // Both menus show each command's key for the OS: Cmd on macOS, Ctrl elsewhere, nothing for an
    // unbound command.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainMenu_Should_ShowThePlatformKeys(bool mac)
    {
        using var window = CreateMainWindow(new KeyboardSettings { IsMac = mac }, new Mock<IPreferencesService>());

        var gestures = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "")
            .Where(n => n.Command is not null)
            .ToDictionary(n => n.Path, n => n.Gesture);

        var command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.Equal(new KeyGesture(Key.S, command), gestures["File/Save"]);
        Assert.Equal(new KeyGesture(Key.S, command | KeyModifiers.Shift), gestures["File/Save As..."]);
        Assert.Equal(new KeyGesture(Key.N, command), gestures["File/New File"]);
        Assert.Equal(mac ? new KeyGesture(Key.Q, KeyModifiers.Meta) : null, gestures["File/Exit"]);
        Assert.Equal(mac ? new KeyGesture(Key.OemComma, KeyModifiers.Meta) : null, gestures["Tools/Preferences..."]);
        Assert.Equal(new KeyGesture(Key.F5), gestures["Run/Run / Continue"]);
        Assert.Null(gestures["Tools/Assembly Explorer..."]);
        Assert.Null(gestures["Help/About Invoklet"]);
    }

    // Rebinding, resetting or unbinding a key in Preferences shows in the open window's menus at
    // once, on the same items and commands.
    [AvaloniaFact]
    public void MainMenu_Should_FollowRebinding()
    {
        var keyboard = new KeyboardSettings { IsMac = false };
        var prefs = new Mock<IPreferencesService>();
        using var window = CreateMainWindow(keyboard, prefs);
        var menu = window.FindControl<Menu>("MainMenu")!;
        var native = MainMenuDefinition.ToNativeMenu(window.MainMenuEntries);
        var itemsBefore = Describe(menu.Items, "").ToList();

        MenuNode Find(IEnumerable<MenuNode> nodes, string path) => nodes.Single(n => n.Path == path);

        keyboard.SetKey(KeyCommands.Save, "Ctrl+Alt+S");
        keyboard.SetKey(KeyCommands.NewFile, "");
        keyboard.SetKey(KeyCommands.OpenAssemblyExplorer, "Ctrl+Shift+E");
        prefs.Raise(p => p.Changed += null);
        MainMenuDefinition.UpdateGestures(window.MainMenuEntries, native);

        var inWindow = Describe(menu.Items, "").ToList();
        var nativeNodes = Describe(native, "").ToList();
        foreach (var nodes in new[] { inWindow, nativeNodes })
        {
            Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt), Find(nodes, "File/Save").Gesture);
            Assert.Null(Find(nodes, "File/New File").Gesture);
            Assert.Equal(new KeyGesture(Key.E, KeyModifiers.Control | KeyModifiers.Shift), Find(nodes, "Tools/Assembly Explorer...").Gesture);
        }
        Assert.Equal(itemsBefore.Select(n => n.Command), inWindow.Select(n => n.Command));

        keyboard.SetKey(KeyCommands.Save, null);
        prefs.Raise(p => p.Changed += null);
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), Find(Describe(menu.Items, ""), "File/Save").Gesture);

        // macOS: the exported system menu bar follows too.
        if (NativeMenu.GetMenu(window) is { } exported)
            Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), Find(Describe(exported, ""), "File/Save").Gesture);
    }

    // EditorTabsControl builds the blank tab it opens on Attach asynchronously (a Task.Run hop, see
    // MainWindowHotkeysTests.WaitForActiveTabControlAsync); tab-level commands no-op until it exists.
    private static async Task<TextEditor> WaitForActiveEditorAsync(Window window)
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (window.GetVisualDescendants().OfType<TabItem>()
                    .Select(t => t.Content).OfType<EditorsTabItemControl>().FirstOrDefault() is { } control
                && control.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault() is { IsEffectivelyVisible: true } editor)
                return editor;
            await Task.Delay(20);
        }

        throw new TimeoutException("The active tab's editor was not created.");
    }

    private static bool IsLeaf(string path)
        => !path.EndsWith("---", StringComparison.Ordinal)
           && !ExpectedMenuTree.Any(p => p.StartsWith(path + "/", StringComparison.Ordinal));

    private sealed record MenuNode(string Path, ICommand? Command, KeyGesture? Gesture = null);

    private static IEnumerable<MenuNode> Describe(IEnumerable<object?> items, string prefix)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case Separator:
                    yield return new MenuNode(prefix + "---", null);
                    break;
                case MenuItem menuItem:
                    var path = prefix + MainMenuDefinition.StripAccessKey((string)menuItem.Header!);
                    yield return new MenuNode(path, menuItem.Command, menuItem.InputGesture);
                    foreach (var child in Describe(menuItem.Items, path + "/")) yield return child;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected menu item {item}");
            }
        }
    }

    private static IEnumerable<MenuNode> Describe(NativeMenu menu, string prefix)
    {
        foreach (var item in menu.Items)
        {
            switch (item)
            {
                case NativeMenuItemSeparator:
                    yield return new MenuNode(prefix + "---", null);
                    break;
                case NativeMenuItem nativeItem:
                    var path = prefix + nativeItem.Header;
                    yield return new MenuNode(path, nativeItem.Command, nativeItem.Gesture);
                    if (nativeItem.Menu is { } sub)
                    {
                        foreach (var child in Describe(sub, path + "/")) yield return child;
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected native menu item {item}");
            }
        }
    }

    [AvaloniaFact]
    public void ApplyPlatformChrome_Should_KeepClientSideDecorations_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var window = CreateMainWindow();

        var windowControlButtons = window.FindControl<StackPanel>("WindowControlButtons");
        Assert.NotNull(windowControlButtons);
        Assert.True(windowControlButtons!.IsVisible);

        var appIcon = window.FindControl<Image>("AppIconImage");
        Assert.NotNull(appIcon);
        Assert.True(appIcon!.IsVisible);
    }

    [AvaloniaFact]
    public async Task RecentMenu_Should_RefreshBothProjections_AndKeepTheExactPathParameter()
    {
        using var window = CreateMainWindow();
        var vm = (MainWindowViewModel)window.DataContext!;
        var windowFile = (MenuItem)window.FindControl<Menu>("MainMenu")!.Items[0]!;
        var windowRecent = windowFile.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Recent _Files");
        var nativeRoot = NativeMenu.GetMenu(window);
        var nativeFile = nativeRoot is null ? null : ((NativeMenuItem)nativeRoot.Items[0]).Menu;
        var nativeRecent = nativeFile?.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Recent Files").Menu;
        var folder = Directory.CreateTempSubdirectory("Invoklet-recent-menu").FullName;
        var path = Path.Combine(folder, "file_with_underscores.rpad");
        try
        {
            File.WriteAllText(path, Invoklet.Format.RPad.RpadWriter.NewFile("1.Dump();", "test"));
            await vm.OpenScriptAsync(path);
            var group = window.MainMenuEntries[0].Items.OfType<MainMenuGroup>().Single(g => g.Header == "Recent _Files");
            var entry = Assert.IsType<MainMenuCommand>(group.Items[0]);
            Assert.Same(windowFile, window.FindControl<Menu>("MainMenu")!.Items[0]);
            Assert.Same(windowRecent, windowFile.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Recent _Files"));
            if (nativeRoot is not null)
            {
                Assert.Same(nativeRoot, NativeMenu.GetMenu(window));
                Assert.Same(nativeFile, ((NativeMenuItem)nativeRoot.Items[0]).Menu);
                Assert.Same(nativeRecent, nativeFile!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Recent Files").Menu);
            }
            Assert.Equal(path, entry.Parameter);
            Assert.Equal(path, MainMenuDefinition.StripAccessKey(entry.Header));
            var native = MainMenuDefinition.ToNativeMenu([group]);
            var item = Assert.IsType<NativeMenuItem>(Assert.IsType<NativeMenuItem>(native.Items[0]).Menu!.Items[0]);
            Assert.Equal(path, item.Header);
            Assert.Equal(path, item.CommandParameter);
            var inWindow = Describe(window.FindControl<Menu>("MainMenu")!.Items, "").ToList();
            var projected = Describe(MainMenuDefinition.ToNativeMenu(window.MainMenuEntries), "").ToList();
            Assert.Equal(inWindow.Select(n => n.Path), projected.Select(n => n.Path));
            await vm.ClearRecentFilesCommand.Execute();
            group = window.MainMenuEntries[0].Items.OfType<MainMenuGroup>().Single(g => g.Header == "Recent _Files");
            Assert.Single(group.Items);
            Assert.Same(windowRecent, windowFile.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "Recent _Files"));
            if (nativeRoot is not null) Assert.Same(nativeRoot, NativeMenu.GetMenu(window));
            Assert.False(vm.HasRecentFiles);
        }
        finally { Directory.Delete(folder, true); }
    }
}
