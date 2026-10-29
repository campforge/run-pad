using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Invoklet.Services.Scaling;

/// <summary>
/// Applies the interface scale to every window of the application. Each window's content is placed
/// in a <see cref="LayoutTransformControl"/> that scales it, so text, spacing, icons and click
/// targets grow together and the layout is done at the scaled size (nothing is just stretched or
/// clipped). Popups — menus, tooltips, flyouts, code completion — take the transform of the control
/// they open from. A window's fixed and minimum sizes grow with it. The native macOS menu bar stays
/// as the system draws it.
/// </summary>
public static class InterfaceScaling
{
    // Marks the wrapper, so the content-changed handler doesn't wrap its own wrapper.
    private sealed class ScaleRoot : LayoutTransformControl;

    // Shown around a window's content only while the scaled content's minimum size doesn't fit on
    // the screen: the window is then kept inside the work area and the content scrolls instead.
    private sealed class FitScroller : ScrollViewer
    {
        public FitScroller()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        protected override Type StyleKeyOverride => typeof(ScrollViewer);

        // The content is laid out at the viewport, so it follows the viewport's size.
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ViewportProperty) (Content as Control)?.InvalidateMeasure();
        }
    }

    // Lays its child out at the scroller's viewport, but never below the window's design minimum (in
    // the content's own, unscaled units). A plain ScrollViewer would measure the content with
    // unlimited width, so stretching layouts and wrapping text would no longer fit the window.
    private sealed class MinSizeHost : Decorator
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var size = TargetSize();
            Child?.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Child?.Arrange(new Rect(finalSize));
            return finalSize;
        }

        private Size TargetSize()
        {
            var viewport = (Parent as ScrollViewer)?.Viewport ?? default;
            return new Size(Math.Max(viewport.Width, MinWidth), Math.Max(viewport.Height, MinHeight));
        }
    }

    // A window's sizes as designed (at 100%), taken before it is first scaled: every scale is applied
    // to these, so going back to 100% restores them exactly.
    private sealed record DesignSize(double Width, double Height, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

    private static readonly AttachedProperty<DesignSize?> DesignSizeProperty =
        AvaloniaProperty.RegisterAttached<Window, DesignSize?>("DesignSize", typeof(InterfaceScaling));

    // Room the window's own frame takes (title bar, borders) while it isn't shown yet and its real
    // frame size is unknown.
    private static readonly Size EstimatedChrome = new(16, 40);

    // Kept free around a fitted window. One that exactly fills the work area is taken by macOS for a
    // zoomed (maximized) window, and a maximized window is left alone by later scale changes.
    private const double EdgeMargin = 16;

    private static bool _resizePending;

    // Replaces the screen's work area (in DIPs) in tests.
    internal static Func<Window, Size?>? WorkAreaOverride { get; set; }

    private static readonly List<Window> ScaledWindows = [];
    private static ViewScale? _source;
    private static IDisposable? _contentHandler;
    private static Style? _popupStyle;

    /// <summary>The scale applied to windows now (1 until <see cref="Attach"/>).</summary>
    public static double Scale { get; private set; } = 1;

    /// <summary>Starts scaling every window opened from now on, and follows changes of <paramref name="source"/>.</summary>
    public static void Attach(ViewScale source)
    {
        Detach();
        _source = source;
        Scale = source.InterfaceScale;
        source.Changed += OnSourceChanged;
        _contentHandler = ContentControl.ContentProperty.Changed.AddClassHandler<Window>(OnWindowContentChanged);
        _popupStyle = new Style(x => x.Is<Popup>())
        {
            Setters = { new Setter(Popup.InheritsTransformProperty, true) },
        };
        Application.Current?.Styles.Add(_popupStyle);
    }

    /// <summary>Stops scaling new windows (tests); windows already scaled keep their scale.</summary>
    public static void Detach()
    {
        if (_source is not null) _source.Changed -= OnSourceChanged;
        _source = null;
        _contentHandler?.Dispose();
        _contentHandler = null;
        if (_popupStyle is not null) Application.Current?.Styles.Remove(_popupStyle);
        _popupStyle = null;
        ScaledWindows.Clear();
        _resizePending = false;
        Scale = 1;
    }

    /// <summary>The scale the content of <paramref name="window"/> is shown at.</summary>
    public static double ScaleOf(Window window) =>
        window.Content is ScaleRoot { LayoutTransform: ScaleTransform t } ? t.ScaleX : 1;

    private static void OnWindowContentChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not Control content || content is ScaleRoot) return;
        // A window built from XAML gets its content in InitializeComponent, before it is shown, so
        // it is first laid out already scaled. The content is the window's child at this point:
        // it is released before the wrapper takes it.
        window.Content = null;
        window.Content = new ScaleRoot
        {
            Child = content,
            LayoutTransform = new ScaleTransform(Scale, Scale),
        };
        window.SetValue(DesignSizeProperty, new DesignSize(
            window.Width, window.Height, window.MinWidth, window.MinHeight, window.MaxWidth, window.MaxHeight));
        ResizeWindow(window, Scale, isMainWindow: false);
        if (!ScaledWindows.Contains(window))
        {
            ScaledWindows.Add(window);
            window.Closed += (_, _) => ScaledWindows.Remove(window);
            // Before it is shown a window's screen and frame are only guessed (primary screen,
            // estimated title bar); once open, it is fitted to the screen it actually opened on.
            window.Opened += (_, _) => ResizeWindow(window, Scale, IsMainWindow(window));
        }
    }

    private static void OnSourceChanged()
    {
        if (_source is null || _source.InterfaceScale == Scale) return;
        Scale = _source.InterfaceScale;
        foreach (var window in ScaledWindows.ToList())
        {
            if (window.Content is ScaleRoot root)
                root.LayoutTransform = new ScaleTransform(Scale, Scale);
        }

        // The windows are resized in a dispatcher job of their own, after the change that caused it
        // (a key press or click in Preferences, each step of a held arrow key) and its layout pass
        // are over. Resized from inside that chain, a macOS window could keep a drawing surface of
        // its old size — the content then showed stretched, clipped or shrunk until the window was
        // reopened. Several quick changes are resized once, to the last scale.
        if (_resizePending) return;
        _resizePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _resizePending = false;
            foreach (var window in ScaledWindows.ToList())
                ResizeWindow(window, Scale, IsMainWindow(window));
        }, DispatcherPriority.Background);
    }

    // Fixed sizes from XAML (a dialog's Width/Height) and the minimum/maximum sizes follow the scale,
    // up to the room the screen's work area leaves for the window's content. A minimum that can't fit
    // there is lowered to it, and the content scrolls. The main window keeps the size the user gave it.
    internal static void ResizeWindow(Window window, double scale, bool isMainWindow)
    {
        if (window.GetValue(DesignSizeProperty) is not { } design) return;
        var room = ClientRoom(window);

        static double Scaled(double value, double scale) => double.IsFinite(value) && value > 0 ? value * scale : value;
        static double Fit(double value, double room) => double.IsFinite(value) ? Math.Min(value, room) : value;
        var minWidth = Fit(Scaled(design.MinWidth, scale), room.Width);
        var minHeight = Fit(Scaled(design.MinHeight, scale), room.Height);
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        window.MaxWidth = Math.Max(Scaled(design.MaxWidth, scale), minWidth);
        window.MaxHeight = Math.Max(Scaled(design.MaxHeight, scale), minHeight);
        if (!isMainWindow && window.WindowState == WindowState.Normal)
        {
            if (double.IsFinite(design.Width)) window.Width = Math.Max(Fit(Scaled(design.Width, scale), room.Width), minWidth);
            if (double.IsFinite(design.Height)) window.Height = Math.Max(Fit(Scaled(design.Height, scale), room.Height), minHeight);
            KeepOnScreen(window);
        }

        var contentDoesNotFit = Scaled(design.MinWidth, scale) > minWidth || Scaled(design.MinHeight, scale) > minHeight;
        SetFitScroller(window, contentDoesNotFit ? design : null);
    }

    private static bool IsMainWindow(Window window) =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow == window;

    // The window's client area can use the work area of its screen less the window frame.
    private static Size ClientRoom(Window window)
    {
        Size? workArea = WorkAreaOverride?.Invoke(window);
        if (workArea is null && (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } screen)
            workArea = new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
        if (workArea is not { } area) return new Size(double.PositiveInfinity, double.PositiveInfinity);

        var chrome = window.IsVisible && window.FrameSize is { } frame
            ? new Size(Math.Max(0, frame.Width - window.ClientSize.Width), Math.Max(0, frame.Height - window.ClientSize.Height))
            : EstimatedChrome;
        return new Size(
            Math.Max(1, area.Width - chrome.Width - EdgeMargin),
            Math.Max(1, area.Height - chrome.Height - EdgeMargin));
    }

    // A dialog that grew may now reach past the edge of its screen: it is moved back inside the work
    // area (only as far as needed).
    private static void KeepOnScreen(Window window)
    {
        if (!window.IsVisible || WorkAreaOverride is not null) return;
        if (window.Screens.ScreenFromWindow(window) is not { } screen) return;
        var chrome = window.FrameSize is { } frame
            ? new Size(Math.Max(0, frame.Width - window.ClientSize.Width), Math.Max(0, frame.Height - window.ClientSize.Height))
            : EstimatedChrome;
        // The size just set may not have reached the native window yet, so it is used when there is one.
        var client = new Size(
            double.IsFinite(window.Width) ? window.Width : window.ClientSize.Width,
            double.IsFinite(window.Height) ? window.Height : window.ClientSize.Height);
        var width = (int)Math.Ceiling((client.Width + chrome.Width) * screen.Scaling);
        var height = (int)Math.Ceiling((client.Height + chrome.Height) * screen.Scaling);
        var area = screen.WorkingArea;
        var x = Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.Right - width));
        var y = Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height));
        if (x != window.Position.X || y != window.Position.Y)
            window.Position = new PixelPoint(x, y);
    }

    private static void SetFitScroller(Window window, DesignSize? design)
    {
        if (window.Content is not ScaleRoot root) return;
        var scroller = root.Child as FitScroller;
        if (design is null)
        {
            if (scroller?.Content is MinSizeHost { Child: { } content } host)
            {
                host.Child = null;
                scroller.Content = null;
                root.Child = content;
            }
            return;
        }

        if (scroller is null)
        {
            var content = root.Child;
            root.Child = null;
            scroller = new FitScroller { Content = new MinSizeHost { Child = content } };
            root.Child = scroller;
        }

        // The design minimum is in the content's own units: the scale transform sits above it.
        var minSize = (MinSizeHost)scroller.Content!;
        minSize.MinWidth = double.IsFinite(design.MinWidth) ? design.MinWidth : 0;
        minSize.MinHeight = double.IsFinite(design.MinHeight) ? design.MinHeight : 0;
        minSize.InvalidateMeasure();
    }

    /// <summary>True while <paramref name="window"/>'s content scrolls because its scaled minimum size doesn't fit.</summary>
    internal static bool IsContentScrolling(Window window) =>
        window.Content is ScaleRoot { Child: FitScroller };
}
