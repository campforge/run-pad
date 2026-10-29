using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Invoklet.Windows;

public partial class WindowTitleBar : UserControl
{
    public WindowTitleBar()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>How the title bar fits the platform; set once by <see cref="WindowChrome"/>.</summary>
    internal static TitleBarMode Mode { get; set; } = TitleBarMode.Native;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var bar = this.FindControl<Border>("Bar")!;
        if (Mode == TitleBarMode.MacOS)
        {
            this.FindControl<StackPanel>("Buttons")!.IsVisible = false;
            this.FindControl<MacWindowButtons>("MacButtons")!.IsVisible = true;
            this.FindControl<TextBlock>("TitleText")!.Margin = new Thickness(4, 0, 12, 0);
        }
        if (Mode != TitleBarMode.Native)
        {
            // Wayland hands a press on a TitleBar-role element straight to the compositor as a move, so
            // a double click never reaches the app; MainWindow drags itself on macOS too. Drag and
            // maximize here instead.
            WindowDecorationProperties.SetElementRole(bar, WindowDecorationsElementRole.None);
            bar.AddHandler(PointerPressedEvent, Bar_PointerPressed);
        }
    }

    private Window? Window => this.FindAncestorOfType<Window>();

    private void Bar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Window is not { } window || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().TakeWhile(v => v != sender)
            .OfType<Control>().Any(control => control.Focusable)) return;
        if (e.ClickCount == 2)
        {
            if (window.CanResize) ToggleMaximized(window);
        }
        else
        {
            window.BeginMoveDrag(e);
        }
        e.Handled = true;
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        if (Window is { } window) window.WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        if (Window is { } window) ToggleMaximized(window);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Window?.Close();

    private static void ToggleMaximized(Window window)
        => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}

internal enum TitleBarMode
{
    /// <summary>The platform drags the window by the title bar and maximizes on a double click (Windows).</summary>
    Native,

    /// <summary>Invoklet drags and maximizes itself (native Wayland).</summary>
    AppDrag,

    /// <summary>Invoklet's own traffic lights (MacWindowButtons) on the left; Invoklet drags and zooms itself.</summary>
    MacOS,
}
