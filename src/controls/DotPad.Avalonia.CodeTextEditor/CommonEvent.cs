using Avalonia.Interactivity;

namespace DotPad.Avalonia.CodeTextEditor;

public static class CommonEvent
{
    public static RoutedEvent Register<TOwner, TEventArgs>(string name, RoutingStrategies routing)
        where TEventArgs : RoutedEventArgs
    {
        return RoutedEvent.Register<TOwner, TEventArgs>(name, routing);
    }
}