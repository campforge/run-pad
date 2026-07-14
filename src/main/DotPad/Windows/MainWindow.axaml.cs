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
        else if (e.Key == Key.F4)
        {
            vm.OpenDependenciesWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.CloseActiveTab();
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

        // TODO: должен быть смособ чтобы делать это красиво, сейчас приходится делать WindowDecorations = Full из за этого меню выглядит не красиво
        // Но если делать WindowDecorations = BorderOnly то меню выглядит красиво, но тогда на Linux нету бордера
        if (OperatingSystem.IsLinux())
        {
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            if (windowControlButtons is not null) windowControlButtons.IsVisible = false;
            // ToDO: скрыть еще иконку из меню
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

    private void NewRpadFileCSharp_Click(object? sender, RoutedEventArgs e)
        => GetViewModel().NewRpadFile(RpadLanguage.CSharp); // Statements kind by default

    private void NewRpadFileVb_Click(object? sender, RoutedEventArgs e)
        => GetViewModel().NewRpadFile(RpadLanguage.Vb); // Statements kind by default

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
