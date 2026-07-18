using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DotPad.Format.RPad;
using DotPad.Services.Preferences;
using DotPad.UserControls;
using DotPad.Windows.Models;

namespace DotPad.Windows;

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

    // TODO.md, "Жизненный цикл вкладок (остаток)": quitting the app used to skip the per-tab
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
    // On macOS, native traffic-light buttons render over the extended client area in the top-left
    // corner — the standard Avalonia-on-macOS pattern — so only the redundant wc-btn cluster needs
    // hiding here. This branch is UNVERIFIED on real hardware (none available in this environment);
    // the icon/menu StackPanel also sitting at top-left may visually collide with the traffic
    // lights and need further adjustment once someone can test on an actual Mac.
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
            // AppIconImage duplicates it immediately below, which is the "лишняя иконка" from the
            // original TODO note. Hide it only where a native titlebar is actually present.
            var appIcon = this.FindControl<Image>("AppIconImage");
            if (appIcon is not null) appIcon.IsVisible = false;
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (windowControlButtons is not null) windowControlButtons.IsVisible = false;
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

    // ── File tree ─────────────────────────────────────────────────────────────

    private void FileTree_Open(object? sender, FileTreeFileEventArgs e)
        => GetViewModel().OpenTab(e.FilePath);

    private void FileTree_Delete(object? sender, FileTreeFileEventArgs e)
        => GetViewModel().DeleteFile(e.FilePath);

    private void FileTree_Rename(object? sender, FileTreeRenameEventArgs e)
        => GetViewModel().RenameFile(e.FilePath, e.NewName);

    private void FileTree_OpenFolder(object? sender, FileTreeFileEventArgs e)
        => GetViewModel().OpenContainingFolder(e.FilePath);

    private void FileTree_CreateFolder(object? sender, FileTreeCreateFolderEventArgs e)
    {
        var newPath = GetViewModel().CreateFolder(e.ParentPath);
        if (sender is FileTreeView view) view.StartRenameForPath(newPath);
    }

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
