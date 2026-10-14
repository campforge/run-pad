using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit;
using ReactiveUI;
using RunPad.Format.RPad;
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

        MainMenuEntries = CreateMainMenu();
        var mainMenu = this.FindControl<Menu>("MainMenu")!;
        foreach (var item in MainMenuDefinition.ToMenuItems(MainMenuEntries)) mainMenu.Items.Add(item);

        ApplyPlatformChrome();

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
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_confirmedClose) return;
        if (_closeConfirmationInProgress)
        {
            e.Cancel = true;
            return;
        }
        if (!_editorTabs.HasUnsavedTabs()) return;

        e.Cancel = true;
        _closeConfirmationInProgress = true;
        bool canClose;
        try
        {
            canClose = await _editorTabs.ConfirmCloseAllUnsavedTabsAsync();
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
        var vm = GetViewModel();

        if (Match(e, kb.Run))
        {
            _editorTabs.RunOrContinueActive();
            e.Handled = true;
        }
        else if (Match(e, kb.Cancel) && vm.ActiveTab?.EnableCancelButton == true)
        {
            _editorTabs.CancelActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepOver))
        {
            _editorTabs.StepOverActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepIn))
        {
            _editorTabs.StepInActive();
            e.Handled = true;
        }
        else if (Match(e, kb.StepOut))
        {
            _editorTabs.StepOutActive();
            e.Handled = true;
        }
        else if (Match(e, kb.Format))
        {
            _editorTabs.FormatActive();
            e.Handled = true;
        }
        else if (Match(e, kb.ResolveAllUsings))
        {
            _editorTabs.ResolveAllUsingsActive();
            e.Handled = true;
        }
        else if (Match(e, kb.GoToLine))
        {
            _editorTabs.GoToLineActive();
            e.Handled = true;
        }
        else if (Match(e, kb.Save))
        {
            _editorTabs.SaveActive();
            e.Handled = true;
        }
        else if (Match(e, kb.SaveAs))
        {
            _editorTabs.SaveAsActive();
            e.Handled = true;
        }
        else if (Match(e, kb.OpenDependencies))
        {
            vm.OpenDependenciesWindow();
            e.Handled = true;
        }
        else if (Match(e, kb.CloseTab))
        {
            _editorTabs.CloseActiveTab();
            e.Handled = true;
        }
        else if (Match(e, kb.NewFile))
        {
            vm.NewRpadFileWithDefaults();
            e.Handled = true;
        }
    }

    private static bool Match(KeyEventArgs e, string gesture)
    {
        try
        {
            return KeyGesture.Parse(gesture).Matches(e);
        }
        catch
        {
            return false;
        }
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
    internal IReadOnlyList<MainMenuGroup> MainMenuEntries { get; }

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
                new MainMenuCommand("_New File", ReactiveCommand.Create(vm.NewRpadFileWithDefaults), Gesture(k => k.NewFile)),
                new MainMenuGroup("New _RPad File",
                [
                    new MainMenuCommand("C#", ReactiveCommand.CreateFromTask(() => NewRpadFileWithKindAsync(RpadLanguage.CSharp))),
                    new MainMenuCommand("Visual Basic", ReactiveCommand.CreateFromTask(() => NewRpadFileWithKindAsync(RpadLanguage.Vb))),
                ]),
                new MainMenuCommand("Open _Text File...", vm.OpenTextFileCommand),
                new MainMenuCommand("_Import LINQPad Query...", vm.ImportLinqFileCommand),
                new MainMenuSeparator(),
                new MainMenuCommand("_Save", ReactiveCommand.Create(tabs.SaveActive), Gesture(k => k.Save)),
                new MainMenuCommand("Save _As...", ReactiveCommand.Create(tabs.SaveAsActive), Gesture(k => k.SaveAs)),
                new MainMenuSeparator(),
                new MainMenuCommand("_Close Tab", ReactiveCommand.Create(tabs.CloseActiveTab), Gesture(k => k.CloseTab)),
                // macOS has Quit in the application menu instead; Close() goes through the same
                // unsaved-changes confirmation as the window's close button.
                .. (OperatingSystem.IsMacOS()
                    ? Array.Empty<MainMenuEntry>()
                    : new MainMenuEntry[] { new MainMenuSeparator(), new MainMenuCommand("E_xit", ReactiveCommand.Create(Close)) }),
            ]),
            new MainMenuGroup("Edit",
            [
                new MainMenuCommand("_Undo", EditCommand(e => e.Undo(), t => t.Undo()), new KeyGesture(Key.Z, KeyModifiers.Control)),
                new MainMenuCommand("_Redo", EditCommand(e => e.Redo(), t => t.Redo()), new KeyGesture(Key.Y, KeyModifiers.Control)),
                new MainMenuSeparator(),
                new MainMenuCommand("Cu_t", EditCommand(e => e.Cut(), t => t.Cut()), new KeyGesture(Key.X, KeyModifiers.Control)),
                new MainMenuCommand("_Copy", EditCommand(e => e.Copy(), t => t.Copy()), new KeyGesture(Key.C, KeyModifiers.Control)),
                new MainMenuCommand("_Paste", EditCommand(e => e.Paste(), t => t.Paste()), new KeyGesture(Key.V, KeyModifiers.Control)),
                new MainMenuCommand("Select _All", EditCommand(e => e.SelectAll(), t => t.SelectAll()), new KeyGesture(Key.A, KeyModifiers.Control)),
                new MainMenuSeparator(),
                new MainMenuCommand("_Format Document", ReactiveCommand.Create(tabs.FormatActive), Gesture(k => k.Format)),
                new MainMenuCommand("_Go to Line...", ReactiveCommand.Create(tabs.GoToLineActive), Gesture(k => k.GoToLine)),
                new MainMenuCommand("_Resolve All Usings", ReactiveCommand.Create(tabs.ResolveAllUsingsActive), Gesture(k => k.ResolveAllUsings)),
            ]),
            // Toggle File Tree is the title-bar sidebar button's command — on macOS that button is
            // hidden with the rest of the custom title row, so the menu is the only way to reach it.
            new MainMenuGroup("View",
            [
                new MainMenuCommand("Toggle _File Tree", vm.Sidebar.ToggleCommand),
                new MainMenuCommand("Toggle _Output Panel", ReactiveCommand.Create(tabs.ToggleOutputPanelActive)),
                new MainMenuSeparator(),
                new MainMenuCommand("Zoom _In", ReactiveCommand.Create(tabs.ZoomInActive)),
                new MainMenuCommand("Zoom O_ut", ReactiveCommand.Create(tabs.ZoomOutActive)),
                new MainMenuCommand("_Reset Zoom", ReactiveCommand.Create(tabs.ResetZoomActive)),
            ]),
            new MainMenuGroup("Run",
            [
                new MainMenuCommand("_Run / Continue", ReactiveCommand.Create(tabs.RunOrContinueActive), Gesture(k => k.Run)),
                new MainMenuCommand("_Stop", ReactiveCommand.Create(tabs.CancelActive), Gesture(k => k.Cancel)),
                new MainMenuSeparator(),
                new MainMenuCommand("_Debug Mode", ReactiveCommand.Create(tabs.ToggleDebugModeActive)),
                new MainMenuSeparator(),
                new MainMenuCommand("Step _Over", ReactiveCommand.Create(tabs.StepOverActive), Gesture(k => k.StepOver)),
                new MainMenuCommand("Step _Into", ReactiveCommand.Create(tabs.StepInActive), Gesture(k => k.StepIn)),
                new MainMenuCommand("Step Ou_t", ReactiveCommand.Create(tabs.StepOutActive), Gesture(k => k.StepOut)),
                new MainMenuCommand("_Pause", ReactiveCommand.Create(tabs.PauseActive)),
                new MainMenuCommand("Run to _Cursor", ReactiveCommand.Create(tabs.RunToCursorActive)),
            ]),
            new MainMenuGroup("Tools",
            [
                new MainMenuCommand("_Dependencies...", vm.OpenDependenciesCommand, Gesture(k => k.OpenDependencies)),
                new MainMenuCommand("_Assembly Explorer...", vm.OpenAssemblyExplorerCommand),
                new MainMenuSeparator(),
                new MainMenuCommand("_Preferences...", vm.OpenPreferencesCommand),
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

    // Customizable shortcuts (Preferences → Keyboard) as menu hints. Read once when the menu is built:
    // a rebound shortcut shows its old hint until restart, while OnWindowKeyDown always reads the
    // current value.
    private KeyGesture? Gesture(Func<KeyboardSettings, string> select)
    {
        try { return KeyGesture.Parse(select(_prefs.Keyboard)); }
        catch { return null; }
    }

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

    // ── File tree ─────────────────────────────────────────────────────────────

    private void FileTree_Open(object? sender, FileTreeFileEventArgs e)
        => GetViewModel().OpenTab(e.FilePath);

    private void FileTree_Delete(object? sender, FileTreeFileEventArgs e)
        => GetViewModel().DeleteFile(e.FilePath);

    private void FileTree_Rename(object? sender, FileTreeRenameEventArgs e)
        => GetViewModel().RenameFile(e.FilePath, e.NewName);

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
        => GetViewModel().MoveFileOrFolder(e.SourcePath, e.TargetPath);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MainWindowViewModel GetViewModel()
        => DataContext as MainWindowViewModel
           ?? throw new InvalidOperationException(
               $"Expected {nameof(MainWindowViewModel)}, got {DataContext?.GetType().Name ?? "null"}");

    public void Dispose()
    {
        _editorTabs.Dispose();
        GC.SuppressFinalize(this);
    }
}
