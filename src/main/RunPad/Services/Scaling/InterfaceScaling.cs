using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace RunPad.Services.Scaling;

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

    // The scale each window's sizes were last multiplied by.
    private static readonly AttachedProperty<double> AppliedScaleProperty =
        AvaloniaProperty.RegisterAttached<Window, double>("AppliedScale", typeof(InterfaceScaling), 1);

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
        ResizeWindow(window, Scale, isMainWindow: false);
        if (!ScaledWindows.Contains(window))
        {
            ScaledWindows.Add(window);
            window.Closed += (_, _) => ScaledWindows.Remove(window);
        }
    }

    private static void OnSourceChanged()
    {
        if (_source is null || _source.InterfaceScale == Scale) return;
        Scale = _source.InterfaceScale;
        var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        foreach (var window in ScaledWindows.ToList())
        {
            if (window.Content is ScaleRoot root)
                root.LayoutTransform = new ScaleTransform(Scale, Scale);
            ResizeWindow(window, Scale, isMainWindow: window == mainWindow);
        }
    }

    // Fixed sizes from XAML (a dialog's Width/Height) and the minimum/maximum sizes follow the scale.
    // The main window keeps the size the user gave it: only its minimum grows.
    internal static void ResizeWindow(Window window, double scale, bool isMainWindow)
    {
        var ratio = scale / window.GetValue(AppliedScaleProperty);
        if (ratio == 1) return;
        window.SetValue(AppliedScaleProperty, scale);

        static double By(double value, double ratio) => double.IsFinite(value) && value > 0 ? value * ratio : value;
        window.MinWidth = By(window.MinWidth, ratio);
        window.MinHeight = By(window.MinHeight, ratio);
        window.MaxWidth = By(window.MaxWidth, ratio);
        window.MaxHeight = By(window.MaxHeight, ratio);
        if (isMainWindow || window.WindowState != WindowState.Normal) return;

        // A scaled-up dialog must still fit on the screen it opens on.
        var workArea = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        var maxWidth = workArea is null ? double.PositiveInfinity : workArea.WorkingArea.Width / workArea.Scaling * 0.95;
        var maxHeight = workArea is null ? double.PositiveInfinity : workArea.WorkingArea.Height / workArea.Scaling * 0.95;
        if (double.IsFinite(window.Width)) window.Width = Math.Min(window.Width * ratio, Math.Max(maxWidth, window.MinWidth));
        if (double.IsFinite(window.Height)) window.Height = Math.Min(window.Height * ratio, Math.Max(maxHeight, window.MinHeight));
    }
}
