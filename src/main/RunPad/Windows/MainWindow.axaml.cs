using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ReactiveUI;
using RunPad.Format.RPad;
using RunPad.Services.Examples;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using RunPad.Windows.Models;

namespace RunPad.Windows;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly IPreferencesService _prefs;
    private readonly EditorTabsControl _editorTabs;

    // Set right before the confirmed, real Close() below — lets OnClosing tell "the user just
    // clicked the confirmed, second time through" apart from the very first Closing that it itself
    // cancels to go resolve unsaved tabs.
    private bool _confirmedClose;

    // A second Close (Cmd+Q / window button again) while the first confirmation is
    // still awaiting a dialog must not start a second, overlapping walk over the dirty tabs.
    private bool _closeConfirmationInProgress;

    public MainWindow(
        IPreferencesService prefs,
        EditorTabsControl editorTabsCtrl,
        MainWindowViewModel viewModel)
    {
        _prefs = prefs;
        _editorTabs = editorTabsCtrl;

        DataContext = viewModel;

        InitializeComponent();

        _keyActions = CreateKeyActions();
        MainMenuEntries = CreateMainMenu();
        _mainMenu = this.FindControl<Menu>("MainMenu")!;
        foreach (var item in MainMenuDefinition.ToMenuItems(MainMenuEntries)) _mainMenu.Items.Add(item);

        ApplyPlatformChrome();

        // A key rebound in Preferences shows in both menus at once (OnWindowKeyDown reads it anyway).
        _prefs.Changed += OnPreferencesChanged;
        Closed += (_, _) => _prefs.Changed -= OnPreferencesChanged;

        // handledEventsToo: true — fires even when AvaloniaEdit marks the event Handled
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        this.FindControl<ContentControl>("EditorTabsHost")!.Content = editorTabsCtrl;
        editorTabsCtrl.Attach(viewModel);

        Closing += OnClosing;
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
        if (!_editorTabs.HasUnsavedTabs() && unsavedWindows.Count == 0) return;

        e.Cancel = true;
        _closeConfirmationInProgress = true;
        bool canClose;
        try
        {
            canClose = await _editorTabs.ConfirmCloseAllUnsavedTabsAsync();
            foreach (var window in unsavedWindows)
            {
                if (!canClose) break;
                canClose = await window.TryCloseAsync();
            }
        }
        finally
        {
            _closeConfirmationInProgress = false;
        }
        if (!canClose) return;

        _confirmedClose = true;
        Close();
    }

    // ── Global keyboard shortcuts ─────────────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
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
        var tabs = _editorTabs;
        return new()
        {
            [KeyCommands.NewFile] = vm.NewRpadFileWithDefaults,
            [KeyCommands.Save] = tabs.SaveActive,
            [KeyCommands.SaveAs] = tabs.SaveAsActive,
            [KeyCommands.CloseTab] = tabs.CloseActiveTab,
            // Close(), as the window's close button: unsaved tabs are asked about first.
            [KeyCommands.Exit] = Close,
            [KeyCommands.OpenDependencies] = vm.OpenDependenciesWindow,
            [KeyCommands.OpenAssemblyExplorer] = () => vm.OpenAssemblyExplorerCommand.Execute().Subscribe(),
            [KeyCommands.OpenPreferences] = () => vm.OpenPreferencesCommand.Execute().Subscribe(),
            [KeyCommands.ToggleFileTree] = () => vm.Sidebar.ToggleCommand.Execute().Subscribe(),
            [KeyCommands.ToggleOutputPanel] = tabs.ToggleOutputPanelActive,
            [KeyCommands.ZoomIn] = tabs.ZoomInActive,
            [KeyCommands.ZoomOut] = tabs.ZoomOutActive,
            [KeyCommands.ResetZoom] = tabs.ResetZoomActive,
            [KeyCommands.RunOrContinue] = tabs.RunOrContinueActive,
            [KeyCommands.Stop] = tabs.CancelActive,
            [KeyCommands.ToggleDebugMode] = tabs.ToggleDebugModeActive,
            [KeyCommands.StepOver] = tabs.StepOverActive,
            [KeyCommands.StepInto] = tabs.StepInActive,
            [KeyCommands.StepOut] = tabs.StepOutActive,
            [KeyCommands.Pause] = tabs.PauseActive,
            [KeyCommands.RunToCursor] = tabs.RunToCursorActive,
            [KeyCommands.Format] = tabs.FormatActive,
            [KeyCommands.GoToLine] = tabs.GoToLineActive,
            [KeyCommands.ResolveAllUsings] = tabs.ResolveAllUsingsActive,
        };
    }

    // ── Per-platform window chrome ──────────────────
    //
    // The XAML defaults (WindowDecorations="BorderOnly" + ExtendClientAreaToDecorationsHint) are a
    // client-side-decoration request: the app draws its own titlebar row (icon, menu, custom
    // wc-btn min/max/close) and asks the OS only for a border. Windows' DWM composites a border,
    // shadow and rounded corners around that automatically — that's what this was designed against.
    //
    // On Linux there's no equivalent guarantee: KWin/GNOME hand back a bare rectangle with no
    // border, shadow, or corner radius for a CSD request (confirmed on this KDE/KWin/Wayland
    // session), and different compositors could behave differently still — there's no portable way
    // to replicate a specific compositor's decoration from inside the app. Falling back to normal
    // OS-drawn decorations is the documented cheaper fix: our custom title row keeps the app
    // icon/menu but becomes a plain menu bar sitting under the native titlebar, and the redundant
    // wc-btn cluster is hidden.
    //
    // On macOS the window gets the stock titlebar — traffic
    // lights, "RunPad" title, system drag/resize/fullscreen — and the menu moves to the system menu
    // bar via NativeMenu, projected from the same MainMenuEntries as the in-window Menu
    //. The whole custom title row would then only duplicate
    // the icon/menu/window buttons, so it's hidden and its 32px row collapsed.
    private void ApplyPlatformChrome()
    {
        var windowControlButtons = this.FindControl<StackPanel>("WindowControlButtons");

        // With WindowDecorations.Full, the OS now draws its own native titlebar
        // (icon + title + min/max/close) above our custom title row. A CSD request
        // (WindowDecorations.BorderOnly, the "pretty menu" branch) was tried first and confirmed —
        // on a real KDE/KWin/Wayland session — to come back as a bare rectangle with no
        // border/shadow/corner-radius on Linux, so there's no portable way to get both a native
        // border AND full CSD control here; Full is the deliberate fallback, not an oversight.
        if (OperatingSystem.IsLinux())
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
        else if (OperatingSystem.IsMacOS())
        {
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;

            this.FindControl<Border>("TitleBar")!.IsVisible = false;
            this.FindControl<Grid>("RootLayout")!.RowDefinitions[0].Height = new GridLength(0);

            NativeMenu.SetMenu(this, MainMenuDefinition.ToNativeMenu(MainMenuEntries));
        }
    }

    // The one menu tree behind both the in-window Menu and
    // the macOS system menu bar (MainMenuDefinition projects it into either). Built in code rather
    // than XAML because NativeMenu items aren't Controls: they don't inherit this window's
    // DataContext, so XAML {Binding} commands on them would silently resolve to nothing.
    //
    // The application ("RunPad") menu is not part of this tree — see App.CreateApplicationMenu.
    internal IReadOnlyList<MainMenuGroup> MainMenuEntries { get; private set; }

    private readonly Menu _mainMenu;

    private void OnPreferencesChanged()
    {
        MainMenuEntries = MainMenuDefinition.WithGestures(MainMenuEntries, _prefs.Keyboard.GestureFor);
        MainMenuDefinition.UpdateGestures(MainMenuEntries, _mainMenu.Items);
        if (NativeMenu.GetMenu(this) is { } nativeMenu) MainMenuDefinition.UpdateGestures(MainMenuEntries, nativeMenu);
    }

    internal const string UserGuideUrl = "https://campforge.github.io/run-pad-docs/";
    internal const string ReleaseNotesUrl = "https://github.com/campforge/run-pad/releases";
    internal const string ReportIssueUrl = "https://github.com/campforge/run-pad/issues/new";

    // A conventional IDE layout, the same on every OS. Every item calls the code behind the matching
    // toolbar button or shortcut (OnWindowKeyDown, EditorTabsControl.*Active), never a copy of it.
    private IReadOnlyList<MainMenuGroup> CreateMainMenu()
    {
        var vm = GetViewModel();
        var tabs = _editorTabs;

        return
        [
            new MainMenuGroup("File",
            [
                Item("_New File", KeyCommands.NewFile),
                new MainMenuGroup("New _RPad File",
                [
                    .. vm.NewFileLanguages.Select(l => new MainMenuCommand(
                        l.DisplayName, ReactiveCommand.CreateFromTask(() => NewRpadFileWithKindAsync(l.Language)))),
                ]),
                new MainMenuCommand("Open _Text File...", vm.OpenTextFileCommand),
                new MainMenuCommand("_Import .linq Query...", vm.ImportLinqFileCommand),
                new MainMenuSeparator(),
                Item("_Save", KeyCommands.Save),
                Item("Save _As...", KeyCommands.SaveAs),
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
                Item("Zoom _In", KeyCommands.ZoomIn),
                Item("Zoom O_ut", KeyCommands.ZoomOut),
                Item("_Reset Zoom", KeyCommands.ResetZoom),
            ]),
            new MainMenuGroup("Run",
            [
                Item("_Run / Continue", KeyCommands.RunOrContinue),
                Item("_Stop", KeyCommands.Stop),
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
                new MainMenuSeparator(),
                Item("_Preferences...", KeyCommands.OpenPreferences),
            ]),
            new MainMenuGroup("Help",
            [
                new MainMenuCommand("_User Guide", OpenLinkCommand(UserGuideUrl)),
                new MainMenuCommand("Release _Notes", OpenLinkCommand(ReleaseNotesUrl)),
                new MainMenuCommand("_Report an Issue", OpenLinkCommand(ReportIssueUrl)),
                new MainMenuSeparator(),
                new MainMenuCommand("_About RunPad", vm.OpenAboutCommand),
            ]),
        ];
    }

    // A menu item for a key-bindable command: the same action as its key, and the key as a hint
    // (kept current by OnPreferencesChanged).
    private MainMenuCommand Item(string header, KeyCommand command)
        => new(header, ReactiveCommand.Create(_keyActions[command]), _prefs.Keyboard.GestureFor(command), command);

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

    private async Task NewRpadFileWithKindAsync(RpadLanguage language)
    {
        var kind = await new NewFileKindDialog().ShowDialog<RpadKind?>(this);
        if (kind is null) return; // cancelled

        GetViewModel().NewRpadFile(language, kind.Value);
    }

    // ── Examples ──────────────────────────────────────────────────────────────

    // A click (also on the example already selected, e.g. after closing its tab) or Enter opens it.
    private void ExampleList_Tapped(object? sender, TappedEventArgs e) => OpenSelectedExample(sender);

    private void ExampleList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        OpenSelectedExample(sender);
        e.Handled = true;
    }

    private void OpenSelectedExample(object? sender)
    {
        if (sender is ListBox { SelectedItem: ExampleScript example })
            GetViewModel().OpenExample(example);
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
        var kind = await new NewFileKindDialog().ShowDialog<RpadKind?>(this);
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
        _prefs.Changed -= OnPreferencesChanged;
        _editorTabs.Dispose();
        GC.SuppressFinalize(this);
    }
}
