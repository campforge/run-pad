using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    // cancels to go ask about unsaved untitled tabs.
    private bool _confirmedClose;

    public MainWindow(
        IPreferencesService prefs,
        EditorTabsControl editorTabsCtrl,
        MainWindowViewModel viewModel)
    {
        _prefs = prefs;
        _editorTabs = editorTabsCtrl;

        DataContext = viewModel;

        InitializeComponent();
        ApplyPlatformChrome();

        // handledEventsToo: true — fires even when AvaloniaEdit marks the event Handled
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        this.FindControl<ContentControl>("EditorTabsHost")!.Content = editorTabsCtrl;
        editorTabsCtrl.Attach(viewModel);

        Closing += OnClosing;
    }

    // TODO.md, "tab lifecycle (leftover)": quitting the app used to skip the per-tab
    // unsaved-changes confirmation entirely, discarding every unsaved untitled tab's content at
    // once. Window.Closing can't itself await a dialog — it has to cancel synchronously first, then
    // (if the user actually wants to proceed) call Close() again for real once the async
    // confirmation resolves; _confirmedClose tells that second, real close apart from the first one
    // this handler itself cancelled.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_confirmedClose) return;
        if (!_editorTabs.HasUnsavedUntitledTabs()) return;

        e.Cancel = true;
        if (!await _editorTabs.ConfirmCloseAllUnsavedTabsAsync()) return;

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

    // ── Per-platform window chrome (ANALYSIS.md feature #15) ──────────────────
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
    // On macOS (docs/handoffs/macos-native-header.md) the window gets the stock titlebar — traffic
    // lights, "RunPad" title, system drag/resize/fullscreen — and the menu moves to the system menu
    // bar via NativeMenu (see ApplyMacNativeMenu). The whole custom title row would then only
    // duplicate the icon/menu/window buttons, so it's hidden and its 32px row collapsed.
    private void ApplyPlatformChrome()
    {
        var windowControlButtons = this.FindControl<StackPanel>("WindowControlButtons");

        // TODO.md #42 — with WindowDecorations.Full, the OS now draws its own native titlebar
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

            ApplyMacNativeMenu();
        }
    }

    // NativeMenu items aren't Controls — they don't inherit this window's DataContext, so XAML
    // {Binding} commands on them would silently resolve to nothing. The menu is built here instead,
    // pointing straight at the same view-model commands / handlers the in-window Menu uses.
    //
    // No NativeMenuItem.Gesture: KeyboardSettings gestures are Ctrl-based and already dispatched by
    // OnWindowKeyDown; registering them as menu key equivalents too would risk running an action
    // twice per keypress.
    private void ApplyMacNativeMenu()
    {
        var vm = GetViewModel();

        var newKindMenu = new NativeMenu
        {
            new NativeMenuItem("C#") { Command = ReactiveCommand.CreateFromTask(() => NewRpadFileWithKindAsync(RpadLanguage.CSharp)) },
            new NativeMenuItem("Visual Basic") { Command = ReactiveCommand.CreateFromTask(() => NewRpadFileWithKindAsync(RpadLanguage.Vb)) },
        };

        var fileMenu = new NativeMenu
        {
            new NativeMenuItem("New File") { Command = ReactiveCommand.Create(vm.NewRpadFileWithDefaults) },
            new NativeMenuItem("New RPad File") { Menu = newKindMenu },
            new NativeMenuItem("Import LINQPad Query...") { Command = vm.ImportLinqFileCommand },
            new NativeMenuItemSeparator(),
            new NativeMenuItem("Save") { Command = ReactiveCommand.Create(_editorTabs.SaveActive) },
            new NativeMenuItem("Save As...") { Command = ReactiveCommand.Create(_editorTabs.SaveAsActive) },
        };

        var editMenu = new NativeMenu
        {
            new NativeMenuItem("Dependencies") { Command = vm.OpenDependenciesCommand },
            new NativeMenuItem("Assembly Explorer...") { Command = vm.OpenAssemblyExplorerCommand },
        };

        var viewMenu = new NativeMenu
        {
            new NativeMenuItem("Toggle File Tree") { Command = vm.Sidebar.ToggleCommand },
        };

        NativeMenu.SetMenu(this, new NativeMenu
        {
            new NativeMenuItem("File") { Menu = fileMenu },
            new NativeMenuItem("Edit") { Menu = editMenu },
            new NativeMenuItem("View") { Menu = viewMenu },
        });

        // Application menu ("RunPad"): About/Preferences take the place of the in-window Help/Edit
        // entries. Avalonia's macOS exporter appends the standard Hide/Show All/Quit items itself;
        // Quit closes windows through the normal Closing path, so OnClosing's unsaved-tab check
        // still runs.
        if (Application.Current is { } app)
        {
            NativeMenu.SetMenu(app, new NativeMenu
            {
                new NativeMenuItem("About RunPad") { Command = vm.OpenAboutCommand },
                new NativeMenuItemSeparator(),
                new NativeMenuItem("Preferences...") { Command = vm.OpenPreferencesCommand },
            });
        }
    }

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

    private void NewRpadFileWithDefaults_Click(object? sender, RoutedEventArgs e) => GetViewModel().NewRpadFileWithDefaults();

    private async void NewRpadFileCSharp_Click(object? sender, RoutedEventArgs e)
        => await NewRpadFileWithKindAsync(RpadLanguage.CSharp);

    private async void NewRpadFileVb_Click(object? sender, RoutedEventArgs e)
        => await NewRpadFileWithKindAsync(RpadLanguage.Vb);

    private async Task NewRpadFileWithKindAsync(RpadLanguage language)
    {
        var kind = await new NewFileKindDialog().ShowDialog<RpadKind?>(this);
        if (kind is null) return; // cancelled

        GetViewModel().NewRpadFile(language, kind.Value);
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => _editorTabs.SaveActive();

    private void SaveAs_Click(object? sender, RoutedEventArgs e) => _editorTabs.SaveAsActive();

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
