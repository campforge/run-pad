using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using RunPad.Desktop;

namespace RunPad.Windows;

/// <summary>
/// One title bar design for every window on every system: each window but MainWindow, which draws
/// its own title row, gets RunPad's title bar (WindowTitleBar, Resources/WindowChrome.axaml) instead of
/// the system's, with the frame around it left to the platform — the system's border and shadow on
/// Windows, AppKit's title bar with its traffic lights on macOS, Avalonia's drawn frame on native
/// Wayland (Resources/WindowDecorations.axaml). Where the system has to draw the title bar itself
/// (Linux on XWayland or X11, where a client-drawn one comes back as a bare rectangle), windows keep it.
/// </summary>
internal static class WindowChrome
{
    private const double TitleBarHeight = 32;

    // Avalonia's drawn frame: 8px corner radius, minus its 1px stroke for the content inside it.
    private const double ContentRadius = 7;

    public static void Install(Application application, IDesktopIntegration desktop)
    {
        var frame = desktop.MainWindowFrame;
        if (frame == WindowFrame.SystemTitleBar) return;
        WindowTitleBar.Mode = frame switch
        {
            WindowFrame.DrawnFrame => TitleBarMode.AppDrag,
            WindowFrame.MacOS => TitleBarMode.MacOS,
            _ => TitleBarMode.Native,
        };
        var theme = (ControlTheme)application.FindResource("RunPadChromeWindow")!;
        // Raised as a window starts showing, before its first layout and styling.
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Apply(window, theme, frame));
    }

    private static void Apply(Window window, ControlTheme theme, WindowFrame frame)
    {
        // MainWindow has its own title row; windows without decorations (popups such as
        // GoToLineDialog, the splash screen) have none.
        if (window is MainWindow || window.WindowDecorations != WindowDecorations.Full) return;

        window.Theme = theme;
        // macOS keeps its title bar for the traffic lights, with the content drawn under it.
        window.WindowDecorations = frame == WindowFrame.MacOS ? WindowDecorations.Full : WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        // The title bar now takes room the system's took outside the window: a fixed size grows by it.
        if (!double.IsNaN(window.Height) && !window.SizeToContent.HasFlag(SizeToContent.Height))
            window.Height += TitleBarHeight;
        if (window.MinHeight > 0) window.MinHeight += TitleBarHeight;

        if (frame == WindowFrame.DrawnFrame)
        {
            // Avalonia draws frame and shadow into WindowDecorationMargin; outside the body the window
            // stays see-through, and the body is rounded to match the frame unless maximized.
            window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            void Round() => window.Classes.Set("rounded", window.WindowState == WindowState.Normal);
            Round();
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) Round();
            };
        }
    }

    /// <summary>
    /// For MainWindow on native Wayland, which extends into its decorations and draws its own title
    /// row. Avalonia draws the frame and shadow into WindowDecorationMargin but leaves that strip to
    /// the window: keep it transparent, inset <paramref name="root"/> past it and round its corners.
    /// </summary>
    public static void FitExtendedWindow(Window window, Panel root)
    {
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        window.Background = Brushes.Transparent;
        root.Bind(Panel.BackgroundProperty, root.GetResourceObservable("SystemRegionBrush"));
        root[!Layoutable.MarginProperty] = window[!Window.WindowDecorationMarginProperty];

        void Clip()
        {
            var radius = window.WindowState == WindowState.Normal ? ContentRadius : 0;
            root.Clip = new RectangleGeometry(new Rect(root.Bounds.Size), radius, radius);
        }
        root.SizeChanged += (_, _) => Clip();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) Clip();
        };
    }
}
