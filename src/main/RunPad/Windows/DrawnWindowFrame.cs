using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RunPad.Services.Desktop;

namespace RunPad.Windows;

/// <summary>
/// Fits window content to the rounded frame Avalonia draws on native Wayland
/// (Resources/WindowDecorations.axaml). Elsewhere the system draws the frame and nothing here runs.
/// </summary>
internal static class DrawnWindowFrame
{
    // The frame's 8px corner radius minus its 1px stroke.
    private const double ContentRadius = 7;

    /// <summary>Rounds the content corners of every window that opens from now on.</summary>
    public static void Install(IDesktopSession desktop)
    {
        if (!desktop.NativeWaylandActive) return;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => FitDecoratedWindow(window));
    }

    /// <summary>
    /// For a window that extends into its decorations and draws its own title row (MainWindow).
    /// Avalonia draws the frame and shadow into WindowDecorationMargin but leaves that strip to the
    /// window: keep it transparent, inset <paramref name="root"/> past it and round its corners.
    /// </summary>
    public static void FitExtendedWindow(Window window, Panel root)
    {
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        window.Background = Brushes.Transparent;
        root.Bind(Panel.BackgroundProperty, root.GetResourceObservable("SystemRegionBrush"));
        root[!Layoutable.MarginProperty] = window[!Window.WindowDecorationMarginProperty];

        void Clip() => root.Clip = RoundedClip(root.Bounds.Size, window.WindowState, top: true);
        root.SizeChanged += (_, _) => Clip();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) Clip();
        };
    }

    // A window with the drawn title bar: Avalonia insets the window below it, so only the bottom
    // corners of the content meet the rounded frame. Windows without decorations (popups such as
    // GoToLineDialog) and MainWindow, which fits itself, are left alone.
    private static void FitDecoratedWindow(Window window)
    {
        if (window.WindowDecorations != WindowDecorations.Full || window.ExtendClientAreaToDecorationsHint) return;

        void Clip() => window.Clip = RoundedClip(window.Bounds.Size, window.WindowState, top: false);
        Clip();
        window.SizeChanged += (_, _) => Clip();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) Clip();
        };
    }

    // Square when maximized or full screen, where the frame is square too.
    private static Geometry? RoundedClip(Size size, WindowState state, bool top)
    {
        if (state != WindowState.Normal || size.Width <= 0 || size.Height <= 0) return null;

        var r = Math.Min(ContentRadius, Math.Min(size.Width, size.Height) / 2);
        var topR = top ? r : 0;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(topR, 0), isFilled: true);
            ctx.LineTo(new Point(size.Width - topR, 0));
            if (top) ctx.ArcTo(new Point(size.Width, r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(size.Width, size.Height - r));
            ctx.ArcTo(new Point(size.Width - r, size.Height), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r, size.Height));
            ctx.ArcTo(new Point(0, size.Height - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(0, topR));
            if (top) ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: true);
        }
        return geometry;
    }
}
