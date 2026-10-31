using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Invoklet.Desktop;
using Invoklet.Format.MdRun;
using Invoklet.Services.Desktop;
using Invoklet.Services.Preferences;
using Invoklet.UserControls;
using Invoklet.UserControls.Models;
using Invoklet.Windows.Healpers;
using Invoklet.Windows.Models;
using ReactiveUI;

namespace Invoklet.Windows;

public sealed partial class MainWindow : Window, IDisposable, IStartupWindow
{
    private readonly IPreferencesService _prefs;
    private readonly EditorTabsControl _editorTabs;
    private readonly IDisposable _restoreStateSubscription;

    Task IStartupWindow.Ready => _editorTabs.WorkspaceShown;

    // Set right before the confirmed, real Close() below — lets OnClosing tell "the user just
    // clicked the confirmed, second time through" apart from the very first Closing that it itself
    // cancels to go resolve unsaved tabs.
    private bool _confirmedClose;

    // A second Close (Cmd+Q / window button again) while the first confirmation is
    // still awaiting a dialog must not start a second, overlapping walk over the dirty tabs.
    private bool _closeConfirmationInProgress;
    private bool _disposed;

    public MainWindow(
        IPreferencesService prefs,
        EditorTabsControl editorTabsCtrl,
        MainWindowViewModel viewModel,
        IDesktopIntegration? desktop = null)
    {
        _prefs = prefs;
        _desktop = desktop ?? DesktopIntegration.Current;
        _editorTabs = editorTabsCtrl;

        DataContext = viewModel;

        InitializeComponent();

        _keyActions = CreateKeyActions();
        MainMenuEntries = CreateMainMenu();
        _mainMenu = this.FindControl<Menu>("MainMenu")!;
        foreach (var item in MainMenuDefinition.ToMenuItems(MainMenuEntries)) _mainMenu.Items.Add(item);

        ApplyPlatformChrome();
        RestoreWindowSize();
        Closed += (_, _) => SaveWindowSize();
        viewModel.RecentFilesChanged += OnRecentFilesChanged;
        Closed += (_, _) => viewModel.RecentFilesChanged -= OnRecentFilesChanged;

        // A key rebound in Preferences shows in both menus at once (OnWindowKeyDown reads it anyway).
        _prefs.Changed += OnPreferencesChanged;
        Closed += (_, _) => _prefs.Changed -= OnPreferencesChanged;

        // handledEventsToo: true — fires even when AvaloniaEdit marks the event Handled
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        foreach (var section in new[] { ConnectionsBundle, ScriptsBundle, SamplesBundle })
        {
            section.GetObservable(Expander.IsExpandedProperty).Subscribe(_ => UpdateSidebarRows());
            section.GetObservable(IsVisibleProperty).Subscribe(_ => UpdateSidebarRows());
        }

        var editorHost = this.FindControl<ContentControl>("EditorTabsHost")!;
        editorHost.Content = editorTabsCtrl;
        // A connection dragged from the sidebar's tree onto the editor is added to the active script.
        // handledEventsToo: the code editor answers text drags itself and leaves this one at None.
        DragDrop.SetAllowDrop(editorHost, true);
        editorHost.AddHandler(DragDrop.DragEnterEvent, EditorHost_DragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        editorHost.AddHandler(DragDrop.DragOverEvent, EditorHost_DragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        editorHost.AddHandler(DragDrop.DropEvent, EditorHost_Drop, RoutingStrategies.Bubble, handledEventsToo: true);
        _restoreStateSubscription = viewModel.WhenAnyValue(vm => vm.IsRestoringWorkspace).Subscribe(restoring =>
        {
            if (!_closeConfirmationInProgress) IsEnabled = !restoring;
            if (NativeMenu.GetMenu(this) is { } native)
                foreach (var item in native.Items.OfType<NativeMenuItem>()) item.IsEnabled = !restoring;
        });
        Closed += (_, _) => _restoreStateSubscription.Dispose();
        editorTabsCtrl.Attach(viewModel);

        // Coming back to Invoklet from another program is when its files were most likely changed
        // there; scripts outside the watched scripts folder are only noticed this way.
        Activated += (_, _) => _ = editorTabsCtrl.CheckFilesChangedOnDiskAsync();

        Closing += OnClosing;
        // The window owns the per-window view model and controls. Dispose them on every
        // successful close so a recreated MainWindow cannot retain subscriptions or tabs.
        Closed += (_, _) => Dispose();
    }

    // Quitting the app used to skip the per-tab
    // unsaved-changes confirmation entirely, discarding every unsaved untitled tab's content at
    // once. Window.Closing can't itself await a dialog — it has to cancel synchronously first, then
    // (if the user actually wants to proceed) call Close() again for real once the async
    // confirmation resolves; _confirmedClose tells that second, real close apart from the first one
    // this handler itself cancelled.
    //
    // The text editor windows this window owns leave their unsaved changes to it when it closes (see
    // TextEditorWindow): each is asked here after the tabs and closed once answered, so closing the
    // app asks once per unsaved document and then really closes.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_confirmedClose) return;
        if (_closeConfirmationInProgress)
        {
            e.Cancel = true;
            return;
        }
        var unsavedWindows = OwnedWindows.OfType<TextEditorWindow>().Where(w => w.ViewModel.IsDirty).ToList();
        if (!GetViewModel().RestoresDrafts && !_editorTabs.HasUnsavedTabs() && unsavedWindows.Count == 0) return;

        e.Cancel = true;
        _closeConfirmationInProgress = true;
        bool canClose;
        try
        {
            IsEnabled = false;
            canClose = (GetViewModel().RestoresDrafts ? await GetViewModel().ConfirmExitWithDraftsAsync() : null)
                ?? await _editorTabs.ConfirmCloseAllUnsavedTabsAsync();
            foreach (var window in unsavedWindows)
            {
                if (!canClose) break;
                canClose = await window.TryCloseAsync();
            }
        }
        finally
        {
            IsEnabled = true;
            _closeConfirmationInProgress = false;
        }
        if (!canClose) return;

        _confirmedClose = true;
        Close();
    }

    // ── Global keyboard shortcuts ─────────────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (GetViewModel().IsRestoringWorkspace) return;
        var kb = _prefs.Keyboard;
        foreach (var (command, action) in _keyActions)
        {
            if (!kb.Matches(e, command)) continue;
            // Stop is only taken while something runs, so the key stays free otherwise.
            if (command == KeyCommands.Stop && GetViewModel().ActiveTab?.EnableCancelButton != true) return;
            action();
            e.Handled = true;
            return;
        }
    }

    // Window-wide commands and what they do — behind both the key bindings above and the menu.
    // Editor-only keys (completion, F12…) are handled by the editor itself; see EditorTabItemControl.
    private readonly IReadOnlyDictionary<KeyCommand, Action> _keyActions;

    private Dictionary<KeyCommand, Action> CreateKeyActions()
    {
        var vm = GetViewModel();
        var actions = new Dictionary<KeyCommand, Action>
        {
            [KeyCommands.NewFile] = vm.NewMdRunFileWithDefaults,
            [KeyCommands.OpenFile] = () => vm.OpenFileCommand.Execute().Subscribe(),
            [KeyCommands.QuickOpen] = () => vm.QuickOpenCommand.Execute().Subscribe(),
            // Close(), as the window's close button: unsaved tabs are asked about first.
            [KeyCommands.Exit] = Close,
            [KeyCommands.OpenDependencies] = vm.OpenDependenciesWindow,
            [KeyCommands.OpenAssemblyExplorer] = () => vm.OpenAssemblyExplorerCommand.Execute().Subscribe(),
            [KeyCommands.OpenPreferences] = () => vm.OpenPreferencesCommand.Execute().Subscribe(),
            [KeyCommands.ToggleFileTree] = () => vm.Sidebar.ToggleCommand.Execute().Subscribe(),
        };
        // The commands on the tab being looked at are the strip's.
        foreach (var (command, action) in _editorTabs.Commands.Bindings) actions.Add(command, action);
        return actions;
    }

    // ── Per-platform window chrome ──────────────────
    //
    // The XAML defaults (WindowDecorations="BorderOnly" + ExtendClientAreaToDecorationsHint) are a
    // client-side-decoration request: the app draws its own titlebar row (icon, menu, custom
    // wc-btn min/max/close) and asks the OS only for a border. Windows' DWM composites a border,
    // shadow and rounded corners around that automatically — that's what this was designed against.
    //
    // The OS's desktop integration says which frame the window gets (IDesktopIntegration).
    // DrawnFrame (Linux's native Wayland): Avalonia draws the frame and shadow itself (see
    // ApplyDrawnFrame), so the Windows layout is kept. SystemTitleBar (Linux on XWayland/X11, where
    // a CSD request comes back as a bare rectangle with no border, shadow, or corner radius): our
    // title row keeps the menu but becomes a plain menu bar under the native titlebar, and the
    // redundant wc-btn cluster is hidden.
    //
    // MacOS keeps AppKit's titlebar as a separate row above the app toolbar, like Rider.
    private void ApplyPlatformChrome()
    {
        var windowControlButtons = this.FindControl<StackPanel>("WindowControlButtons");

        var frame = _desktop.MainWindowFrame;
        if (frame == WindowFrame.DrawnFrame)
        {
            ApplyDrawnFrame();
        }
        else if (frame == WindowFrame.SystemTitleBar)
        {
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            if (windowControlButtons is not null) windowControlButtons.IsVisible = false;

            // The native titlebar already renders Window.Icon (see the XAML root element) — our own
            // AppIconImage duplicates it immediately below, which is the "extra icon" from the
            // original TODO note. Hide it only where a native titlebar is actually present.
            var appIcon = this.FindControl<Image>("AppIconImage");
            if (appIcon is not null) appIcon.IsVisible = false;
        }
        else if (frame == WindowFrame.MacOS)
        {
            WindowDecorations = WindowDecorations.BorderOnly;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 32;
            this.FindControl<Menu>("MainMenu")!.IsVisible = false;
            this.FindControl<Image>("AppIconImage")!.IsVisible = false;
            windowControlButtons!.IsVisible = false;
            this.FindControl<MacWindowButtons>("MacWindowButtons")!.IsVisible = true;
            var sidebarToggle = this.FindControl<Button>("SidebarToggleButton");
            sidebarToggle!.Width = 30;
            this.FindControl<PathIcon>("SidebarToggleIcon")!.Width = 14;
            this.FindControl<PathIcon>("SidebarToggleIcon")!.Height = 14;
            this.FindControl<Border>("TitleBar")!.AddHandler(PointerPressedEvent, OnCustomTitleBarPointerPressed);

            NativeMenu.SetMenu(this, MainMenuDefinition.ToNativeMenu(MainMenuEntries));
        }
    }

    // Size only, not position: native Wayland never tells a window where it is, and the window
    // manager places it sensibly on every system.
    private void RestoreWindowSize()
    {
        var layout = _prefs.Layout;
        if (layout is { WindowWidth: > 0 and var width, WindowHeight: > 0 and var height })
        {
            Width = width;
            Height = height;
        }
        if (layout.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowSize()
    {
        var layout = _prefs.Layout;
        layout.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            layout.WindowWidth = ClientSize.Width;
            layout.WindowHeight = ClientSize.Height;
        }
        _prefs.Save();
    }

    // Native Wayland: Avalonia draws the frame and shadow (see WindowChrome), this window keeps
    // its own title row.
    private void ApplyDrawnFrame()
    {
        WindowChrome.FitExtendedWindow(this, this.FindControl<Grid>("RootLayout")!);

        // Wayland hands a press on a TitleBar-role element straight to the compositor as a move, so
        // the app never sees the double click that should maximize. Drag and maximize here instead.
        var titleBar = this.FindControl<Border>("TitleBar")!;
        WindowDecorationProperties.SetElementRole(titleBar, WindowDecorationsElementRole.None);
        titleBar.AddHandler(PointerPressedEvent, OnCustomTitleBarPointerPressed);
    }

    // The one menu tree behind both the in-window Menu and
    // the macOS system menu bar (MainMenuDefinition projects it into either). Built in code rather
    // than XAML because NativeMenu items aren't Controls: they don't inherit this window's
    // DataContext, so XAML {Binding} commands on them would silently resolve to nothing.
    //
    // The application ("Invoklet") menu is not part of this tree — see App.CreateApplicationMenu.
    internal IReadOnlyList<MainMenuGroup> MainMenuEntries { get; private set; }

    private readonly Menu _mainMenu;
    private readonly IDesktopIntegration _desktop;

    private void OnPreferencesChanged()
    {
        MainMenuEntries = MainMenuDefinition.WithGestures(MainMenuEntries, _prefs.Keyboard.GestureFor);
        MainMenuDefinition.UpdateGestures(MainMenuEntries, _mainMenu.Items);
        if (NativeMenu.GetMenu(this) is { } nativeMenu) MainMenuDefinition.UpdateGestures(MainMenuEntries, nativeMenu);
    }

    internal const string UserGuideUrl = "https://tervforge.github.io/invoklet-docs/";
    internal const string ReleaseNotesUrl = "https://github.com/tervforge/invoklet/releases";
    internal const string ReportIssueUrl = "https://github.com/tervforge/invoklet/issues/new";

    // A conventional IDE layout, the same on every OS. Every item calls the code behind the matching
    // toolbar button or shortcut (OnWindowKeyDown, EditorTabsControl.*Active), never a copy of it.
    private MainMenuGroup CreateRecentFilesMenu()
    {
        var vm = GetViewModel();
        var entries = vm.RecentFiles.Select(path => (MainMenuEntry)new MainMenuCommand(
            path.Replace("_", "__"), vm.OpenRecentFileCommand, Parameter: path)).ToList();
        if (entries.Count > 0) entries.Add(new MainMenuSeparator());
        entries.Add(new MainMenuCommand("_Clear Recent Files", vm.ClearRecentFilesCommand));
        return new MainMenuGroup("Recent _Files", entries);
    }

    private void OnRecentFilesChanged()
    {
        // The macOS exporter tracks the identity of each exported menu. Replacing the root (or
        // submenu) while it handles a command invalidates that tracking; update only its items.
        for (var groupIndex = 0; groupIndex < MainMenuEntries.Count; groupIndex++)
        {
            var group = MainMenuEntries[groupIndex];
            for (var itemIndex = 0; itemIndex < group.Items.Count; itemIndex++)
            {
                if (group.Items[itemIndex] is not MainMenuGroup { Header: "Recent _Files" }) continue;
                var recent = CreateRecentFilesMenu();
                var groups = MainMenuEntries.ToArray();
                var children = group.Items.ToArray();
                children[itemIndex] = recent;
                groups[groupIndex] = group with { Items = children };
                MainMenuEntries = groups;

                var menuItem = (MenuItem)((MenuItem)_mainMenu.Items[groupIndex]!).Items[itemIndex]!;
                menuItem.Items.Clear();
                foreach (var child in MainMenuDefinition.ToMenuItems(recent.Items)) menuItem.Items.Add(child);

                if (NativeMenu.GetMenu(this) is { } nativeRoot)
                {
                    var nativeGroup = ((NativeMenuItem)nativeRoot.Items[groupIndex]).Menu!;
                    var nativeRecent = ((NativeMenuItem)nativeGroup.Items[itemIndex]).Menu!;
                    nativeRecent.Items.Clear();
                    foreach (var child in recent.Items) nativeRecent.Add(MainMenuDefinition.ToNativeMenuItem(child));
                }
                return;
            }
        }
    }

    private IReadOnlyList<MainMenuGroup> CreateMainMenu()
    {
        var vm = GetViewModel();
        var tabs = _editorTabs;

        return
        [
            new MainMenuGroup("File",
            [
                Item("_New File", KeyCommands.NewFile),
                new MainMenuGroup("New _Invoklet File",
                [
                    .. vm.NewFileLanguages.Select(l => new MainMenuCommand(
                        l.DisplayName, ReactiveCommand.CreateFromTask(() => NewMdRunFileWithKindAsync(l.Language)))),
                ]),
                new MainMenuCommand("_Open...", vm.OpenFileCommand, _prefs.Keyboard.GestureFor(KeyCommands.OpenFile), KeyCommands.OpenFile),
                new MainMenuCommand("_Quick Open...", vm.QuickOpenCommand, _prefs.Keyboard.GestureFor(KeyCommands.QuickOpen), KeyCommands.QuickOpen),
                CreateRecentFilesMenu(),
                new MainMenuCommand("Open _Text File...", vm.OpenTextFileCommand),
                new MainMenuCommand("_Import .linq Query...", vm.ImportLinqFileCommand),
                new MainMenuSeparator(),
                Item("_Save", KeyCommands.Save),
                Item("Save _As...", KeyCommands.SaveAs),
                Item("Export _Results as HTML...", KeyCommands.ExportResults),
                Item("Re_vert to Saved", KeyCommands.RevertFile),
                new MainMenuSeparator(),
                Item("_Close Tab", KeyCommands.CloseTab),
                new MainMenuSeparator(),
                Item("E_xit", KeyCommands.Exit),
            ]),
            new MainMenuGroup("Edit",
            [
                new MainMenuCommand("_Undo", EditCommand(e => e.Undo(), t => t.Undo()), Hotkey(h => h.Undo)),
                new MainMenuCommand("_Redo", EditCommand(e => e.Redo(), t => t.Redo()), Hotkey(h => h.Redo)),
                new MainMenuSeparator(),
                new MainMenuCommand("Cu_t", EditCommand(e => e.Cut(), t => t.Cut()), Hotkey(h => h.Cut)),
                new MainMenuCommand("_Copy", EditCommand(e => e.Copy(), t => t.Copy()), Hotkey(h => h.Copy)),
                new MainMenuCommand("_Paste", EditCommand(e => e.Paste(), t => t.Paste()), Hotkey(h => h.Paste)),
                new MainMenuCommand("Select _All", EditCommand(e => e.SelectAll(), t => t.SelectAll()), Hotkey(h => h.SelectAll)),
                new MainMenuSeparator(),
                Item("_Format Document", KeyCommands.Format),
                Item("Format Se_lection", KeyCommands.FormatSelection),
                Item("_Go to Line...", KeyCommands.GoToLine),
                Item("_Resolve All Usings", KeyCommands.ResolveAllUsings),
            ]),
            // Toggle File Tree is the title-bar sidebar button's command — on macOS that button is
            // hidden with the rest of the custom title row, so the menu is the only way to reach it.
            new MainMenuGroup("View",
            [
                Item("Toggle _File Tree", KeyCommands.ToggleFileTree),
                Item("Toggle _Output Panel", KeyCommands.ToggleOutputPanel),
                new MainMenuSeparator(),
                Item("Markdown _Preview", KeyCommands.TogglePreview),
                Item("Markdown Preview to the _Side", KeyCommands.TogglePreviewToSide),
                new MainMenuSeparator(),
                Item("Zoom _In", KeyCommands.ZoomIn),
                Item("Zoom O_ut", KeyCommands.ZoomOut),
                Item("_Reset Zoom", KeyCommands.ResetZoom),
            ]),
            new MainMenuGroup("Run",
            [
                Item("_Run / Continue", KeyCommands.RunOrContinue),
                Item("_Stop", KeyCommands.Stop),
                Item("E_xecute Selection", KeyCommands.ExecuteSelection),
                Item("C_lear Results", KeyCommands.ClearResults),
                Item("Stop and Run _Again", KeyCommands.Restart),
                new MainMenuSeparator(),
                Item("_Debug Mode", KeyCommands.ToggleDebugMode),
                new MainMenuSeparator(),
                Item("Step _Over", KeyCommands.StepOver),
                Item("Step _Into", KeyCommands.StepInto),
                Item("Step Ou_t", KeyCommands.StepOut),
                Item("_Pause", KeyCommands.Pause),
                Item("Run to _Cursor", KeyCommands.RunToCursor),
            ]),
            new MainMenuGroup("Tools",
            [
                Item("_Dependencies...", KeyCommands.OpenDependencies),
                Item("_Assembly Explorer...", KeyCommands.OpenAssemblyExplorer),
                new MainMenuCommand("_Connections...", vm.OpenConnectionsCommand),
                new MainMenuCommand("_Secret Store...", vm.OpenSecretStoreCommand),
                new MainMenuSeparator(),
                Item("_Preferences...", KeyCommands.OpenPreferences),
            ]),
            new MainMenuGroup("Help",
            [
                new MainMenuCommand("_User Guide", OpenLinkCommand(UserGuideUrl)),
                new MainMenuCommand("Release _Notes", OpenLinkCommand(ReleaseNotesUrl)),
                new MainMenuCommand("_Report an Issue", OpenLinkCommand(ReportIssueUrl)),
                new MainMenuSeparator(),
                new MainMenuCommand("_About Invoklet", vm.OpenAboutCommand),
            ]),
        ];
    }

    // A menu item for a key-bindable command: the same action as its key, and the key as a hint
    // (kept current by OnPreferencesChanged).
    private MainMenuCommand Item(string header, KeyCommand command)
        => new(header, ReactiveCommand.Create(() => { if (!GetViewModel().IsRestoringWorkspace) _keyActions[command](); }), _prefs.Keyboard.GestureFor(command), command);

    // Undo, Copy… use the platform's keys (Cmd on macOS). Elsewhere the text boxes handle them and
    // the menu only shows them; on macOS the menu bar takes the key first and its Edit command acts on
    // the focused text control (EditTarget), so each keypress is still handled once.
    private KeyGesture? Hotkey(Func<PlatformHotkeyConfiguration, List<KeyGesture>> select)
        => Application.Current?.PlatformSettings?.HotkeyConfiguration is { } hotkeys ? select(hotkeys).FirstOrDefault() : null;

    private ReactiveCommand<Unit, Unit> OpenLinkCommand(string url)
        => ReactiveCommand.CreateFromTask(async () => { await Launcher.LaunchUriAsync(new Uri(url)); });

    // ── Edit menu target ──────────────────────────────────────────────────────

    // The text control the Edit menu acts on: the focused one, or else the last one that had focus,
    // because opening the in-window menu can take focus away from it.
    private Control? _lastTextTarget;

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is Control control && FindTextTarget(control) is { } target)
            _lastTextTarget = target;
    }

    private static Control? FindTextTarget(Control control)
        => (Control?)control.FindAncestorOfType<TextEditor>(includeSelf: true)
           ?? control.FindAncestorOfType<TextBox>(includeSelf: true);

    internal Control? EditTarget
    {
        get
        {
            var focused = FocusManager?.GetFocusedElement() is Control control ? FindTextTarget(control) : null;
            var target = focused ?? _lastTextTarget;
            // A closed tab's editor is detached from this window; don't act on it.
            return target is not null && TopLevel.GetTopLevel(target) == this ? target : null;
        }
    }

    private ReactiveCommand<Unit, Unit> EditCommand(Action<TextEditor> onEditor, Action<TextBox> onTextBox)
        => ReactiveCommand.Create(() =>
        {
            switch (EditTarget)
            {
                case TextEditor editor:
                    onEditor(editor);
                    editor.TextArea.Focus();
                    break;
                case TextBox textBox:
                    onTextBox(textBox);
                    textBox.Focus();
                    break;
            }
        });

    // ── Title bar window controls ─────────────────────────────────────────────

    private void OnCustomTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;
        if (source.GetSelfAndVisualAncestors().TakeWhile(v => v != sender)
            .OfType<Control>().Any(control => control.Focusable)) return;
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else BeginMoveDrag(e);
        e.Handled = true;
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Menu handlers ─────────────────────────────────────────────────────────

    private async Task NewMdRunFileWithKindAsync(MdRunLanguage language)
    {
        if (GetViewModel().IsRestoringWorkspace) return;
        var kind = await new NewFileKindDialog().ShowDialog<MdRunKind?>(this);
        if (kind is null) return; // cancelled

        GetViewModel().NewMdRunFile(language, kind.Value);
    }

    // ── Samples ───────────────────────────────────────────────────────────────

    // A click on a sample (also the one already selected, e.g. after closing its tab) or Enter opens
    // it; a folder only opens or folds.
    private void SampleTree_Tapped(object? sender, TappedEventArgs e)
    {
        // Control-click is the secondary click on macOS (Services/ControlClick): it never opens a sample.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && OperatingSystem.IsMacOS()) return;
        Visual? visual = e.Source as Visual;
        while (visual is not null and not TreeViewItem) visual = visual.GetVisualParent();
        if (visual is TreeViewItem item && item.DataContext is SampleTreeNode sample)
        {
            if (sample.Example is { } example)
                GetViewModel().OpenExample(example);
            else if (sample.Children is { Count: > 0 })
                item.IsExpanded = !item.IsExpanded;
        }
    }

    private void SampleTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || sender is not TreeView { SelectedItem: SampleTreeNode { Example: { } example } }) return;
        GetViewModel().OpenExample(example);
        e.Handled = true;
    }

    // ── Sidebar sections ──────────────────────────────────────────────────────

    // Open sections share the sidebar's height and each tree scrolls inside its own section; a folded
    // or hidden one takes its header only. The last row takes what is left when all are folded.
    private void UpdateSidebarRows()
    {
        var grid = this.FindControl<Grid>("SidebarSections")!;
        var sections = new[] { ConnectionsBundle, ScriptsBundle, SamplesBundle };
        var anyOpen = false;
        for (var i = 0; i < sections.Length; i++)
        {
            var open = sections[i].IsVisible && sections[i].IsExpanded;
            anyOpen |= open;
            grid.RowDefinitions[i].Height = open ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }
        grid.RowDefinitions[3].Height = anyOpen ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    // ── Connections tree ──────────────────────────────────────────────────────

    private void ConnectionNavigation_OpenConnections(object? sender, ConnectionNavigationEventArgs e)
        => _ = GetViewModel().OpenConnectionsAsync(e.ConnectionName);

    // The Connections header's own button: handled, so the click doesn't also fold the section.
    private void AddConnection_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _ = GetViewModel().OpenConnectionsAsync(null);
    }

    private void ConnectionNavigation_RemoveFromScript(object? sender, ConnectionNavigationEventArgs e)
    {
        if (e.ConnectionName is { } name) GetViewModel().RemoveConnectionFromScript(name);
    }

    private void ConnectionNavigation_AddToScript(object? sender, ConnectionNavigationEventArgs e)
    {
        if (e.ConnectionName is { } name) _ = GetViewModel().AddConnectionToScriptAsync(name);
    }

    private string? DroppableConnection(DragEventArgs e)
        => GetViewModel() is { ActiveTab: not null, ConnectionNavigation.HasActiveScript: true }
            ? ConnectionNavigationView.DraggedConnectionName(e.DataTransfer) : null;

    private void EditorHost_DragOver(object? sender, DragEventArgs e)
    {
        if (DroppableConnection(e) is null) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void EditorHost_Drop(object? sender, DragEventArgs e)
    {
        if (DroppableConnection(e) is not { } name) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        _ = GetViewModel().AddConnectionToScriptAsync(name);
    }

    // ── File tree ─────────────────────────────────────────────────────────────

    private void FileTree_Open(object? sender, FileTreeFileEventArgs e)
        => GetViewModel().OpenTab(e.FilePath);

    // These complete without throwing (failures are shown as errors), so nothing is lost by not awaiting.
    private void FileTree_Delete(object? sender, FileTreeFileEventArgs e)
        => _ = GetViewModel().DeleteFile(e.FilePath);

    private void FileTree_Rename(object? sender, FileTreeRenameEventArgs e)
        => _ = GetViewModel().RenameFile(e.FilePath, e.NewName);

    private void FileTree_OpenFolder(object? sender, FileTreeFileEventArgs e)
        => GetViewModel().OpenContainingFolder(e.FilePath);

    private async void FileTree_CreateFolder(object? sender, FileTreeCreateFolderEventArgs e)
    {
        var newPath = await GetViewModel().CreateFolderAsync(e.ParentPath);
        if (sender is FileTreeView view) view.StartRenameForPath(newPath);
    }

    private async void FileTree_CreateFile(object? sender, FileTreeCreateFileEventArgs e)
    {
        var kind = await new NewFileKindDialog().ShowDialog<MdRunKind?>(this);
        if (kind is null) return; // cancelled

        var newPath = await GetViewModel().CreateFileAsync(e.ParentPath, e.Language, kind.Value);
        if (sender is FileTreeView view) view.StartRenameForPath(newPath);
    }

    private void FileTree_Move(object? sender, FileTreeMoveEventArgs e)
        => _ = GetViewModel().MoveFileOrFolder(e.SourcePath, e.TargetPath);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MainWindowViewModel GetViewModel()
        => DataContext as MainWindowViewModel
           ?? throw new InvalidOperationException(
               $"Expected {nameof(MainWindowViewModel)}, got {DataContext?.GetType().Name ?? "null"}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GetViewModel().RecentFilesChanged -= OnRecentFilesChanged;
        _prefs.Changed -= OnPreferencesChanged;
        _restoreStateSubscription.Dispose();
        _editorTabs.Dispose();
        GetViewModel().Dispose();
        GC.SuppressFinalize(this);
    }
}
