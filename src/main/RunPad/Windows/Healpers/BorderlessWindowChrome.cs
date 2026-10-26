using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace RunPad.Windows;

internal static class BorderlessWindowChrome
{
    // Attach subscribes for the window's lifetime and never detaches, so a second call would only stack
    // handlers (two drags per press); it is refused instead.
    private static readonly AttachedProperty<bool> IsAttachedProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("IsBorderlessChromeAttached", typeof(BorderlessWindowChrome));

    internal static bool IsAttached(Window window) => window.GetValue(IsAttachedProperty);

    /// <summary>Gives a borderless <paramref name="window"/> its themed <paramref name="surface"/> and
    /// lets text and blank space drag it. Once per window: a second call throws.</summary>
    internal static void Attach(Window window, Border surface)
    {
        if (window.GetValue(IsAttachedProperty))
            throw new InvalidOperationException("The borderless window chrome is already attached to this window.");
        window.SetValue(IsAttachedProperty, true);
        var roundedCorners = surface.CornerRadius;
        void UpdateSurface()
        {
            var transparent = window.ActualTransparencyLevel != WindowTransparencyLevel.None;
            window.Background = transparent ? Brushes.Transparent : surface.Background;
            surface.CornerRadius = transparent ? roundedCorners : default;
        }

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty) UpdateSurface();
        };
        surface.PropertyChanged += (_, e) =>
        {
            if (e.Property == Border.BackgroundProperty) UpdateSurface();
        };
        surface.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed || e.Source is not Visual source)
                return;
            // Text and blank space can drag the window; inputs and buttons keep their gestures.
            if (source.GetSelfAndVisualAncestors().TakeWhile(v => v != surface)
                .OfType<Control>().Any(control => control.Focusable)) return;
            window.BeginMoveDrag(e);
        }, RoutingStrategies.Bubble);
        UpdateSurface();
    }
}
