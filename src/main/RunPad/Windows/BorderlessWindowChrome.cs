using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace RunPad.Windows;

internal static class BorderlessWindowChrome
{
    internal static void Attach(Window window, Border surface)
    {
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
