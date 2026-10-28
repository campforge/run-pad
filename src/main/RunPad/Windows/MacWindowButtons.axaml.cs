using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace RunPad.Windows;

public partial class MacWindowButtons : UserControl
{
    private static readonly IBrush[] ActiveColors =
        [Brush.Parse("#FF5F57"), Brush.Parse("#FEBC2E"), Brush.Parse("#28C840")];
    private static readonly IBrush InactiveColor = Brush.Parse("#8E8E93");

    private Window? _window;
    private bool _windowActive = true;

    public MacWindowButtons()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = this.FindAncestorOfType<Window>();
        if (_window is null) return;
        _window.Activated += Window_Activated;
        _window.Deactivated += Window_Deactivated;
        // A window that can't be resized can't be minimized or zoomed either, as with AppKit's dialogs.
        this.FindControl<Button>("MinimizeButton")!.IsEnabled = _window.CanResize;
        this.FindControl<Button>("ZoomButton")!.IsEnabled = _window.CanResize;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is null) return;
        _window.Activated -= Window_Activated;
        _window.Deactivated -= Window_Deactivated;
        _window = null;
    }

    private void Window_Activated(object? sender, EventArgs e) => SetWindowActive(true);

    private void Window_Deactivated(object? sender, EventArgs e) => SetWindowActive(false);

    private void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (!active) SetGlyphsVisible(false);
        SetDotColors(active);
    }

    private void SetDotColors(bool highlighted)
    {
        string[] dots = ["CloseDot", "MinimizeDot", "ZoomDot"];
        for (var i = 0; i < dots.Length; i++)
            this.FindControl<Ellipse>(dots[i])!.Fill = highlighted ? ActiveColors[i] : InactiveColor;
    }

    private void Button_PointerEntered(object? sender, PointerEventArgs e)
    {
        SetDotColors(true);
        SetGlyphsVisible(true);
    }

    private void Buttons_PointerExited(object? sender, PointerEventArgs e)
    {
        SetDotColors(_windowActive);
        if (this.FindControl<StackPanel>("Buttons")?.IsPointerOver != true)
            SetGlyphsVisible(false);
    }

    private void SetGlyphsVisible(bool visible)
    {
        foreach (var button in this.FindControl<StackPanel>("Buttons")!.Children.OfType<Button>())
            if (button.Content is Grid grid && grid.Children.Count > 1)
                ((AvaloniaPath)grid.Children[1]).IsVisible = visible;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _window?.Close();

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        if (_window is { } window) window.WindowState = WindowState.Minimized;
    }

    private void Zoom_Click(object? sender, RoutedEventArgs e)
    {
        if (_window is { } window)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
