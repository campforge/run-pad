using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Invoklet.Desktop.MacOS;

/// <summary>
/// On macOS, Control-click is the secondary click: a context menu and whatever a right click does
/// first, such as selecting the row under the pointer. Avalonia reports it as a left click with
/// Control held, so it is turned into a right click before anything else sees it: the left press
/// and release are stopped at the window, and a right press and release are raised in their place
/// on the same element. Everything that already answers a right click then answers it too.
/// </summary>
public static class ControlClick
{
    // Pointers whose left press was turned into a right one, so their release is turned too.
    private static readonly HashSet<int> Converted = [];
    private static bool _registered;

    /// <summary>Applies to every window, menu and popup of the app; does nothing outside macOS.</summary>
    public static void RegisterForApp()
    {
        if (_registered || !OperatingSystem.IsMacOS()) return;
        _registered = true;
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(OnPressed, RoutingStrategies.Tunnel);
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>(OnReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>The same for one window on any platform, for tests.</summary>
    internal static IDisposable AttachTo(TopLevel topLevel)
    {
        void Pressed(object? sender, PointerPressedEventArgs e) => OnPressed(topLevel, e);
        void Released(object? sender, PointerReleasedEventArgs e) => OnReleased(topLevel, e);
        topLevel.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        topLevel.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        return new Detach(() =>
        {
            topLevel.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
            topLevel.RemoveHandler(InputElement.PointerReleasedEvent, Released);
        });
    }

    private static void OnPressed(TopLevel topLevel, PointerPressedEventArgs e)
    {
        if (e.Handled || e.Pointer.Type != PointerType.Mouse || e.KeyModifiers != KeyModifiers.Control) return;
        var properties = e.GetCurrentPoint(topLevel).Properties;
        if (properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        e.Handled = true;
        // Synthetic/headless pointer events can report the TopLevel as Source even when the
        // pointer is over a child. Resolve the hit-tested control so the converted right click
        // still reaches TreeViewItem selection and context-menu handlers.
        var source = e.Source as Interactive ?? topLevel.InputHitTest(e.GetPosition(topLevel)) as Interactive;
        if (source is null) return;
        Converted.Add(e.Pointer.Id);
        source.RaiseEvent(new PointerPressedEventArgs(source, e.Pointer, topLevel, e.GetPosition(topLevel), e.Timestamp,
            new PointerPointProperties(RawInputModifiers.Control | RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
            KeyModifiers.Control, clickCount: 1));
    }

    private static void OnReleased(TopLevel topLevel, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || !Converted.Remove(e.Pointer.Id)) return;
        e.Handled = true;
        var source = e.Source as Interactive ?? topLevel.InputHitTest(e.GetPosition(topLevel)) as Interactive;
        if (source is null) return;
        source.RaiseEvent(new PointerReleasedEventArgs(source, e.Pointer, topLevel, e.GetPosition(topLevel), e.Timestamp,
            new PointerPointProperties(RawInputModifiers.Control, PointerUpdateKind.RightButtonReleased),
            e.KeyModifiers, MouseButton.Right));
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
