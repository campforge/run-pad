using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RunPad.Desktop.Linux;

/// <summary>
/// Works around Avalonia's Wayland back end repeating a shortcut forever once it opens a dialog.
/// </summary>
/// <remarks>
/// On Wayland the client repeats held keys itself, and Avalonia stops repeating on the key's release
/// or when the keyboard leaves the window. A modal dialog disables its owner first, and a disabled
/// window drops every input event — that release and leave included — while the repeat timer keeps
/// sending the key: Ctrl+G reopened Go to Line again and again, and the dialog itself kept getting
/// a G it never saw released. A window that gains or loses focus has no key held for it any more,
/// so its repeat is stopped then; activation reaches the window directly, not through that input
/// path. Installed only with the native back end, and if Avalonia renames these members the calls do nothing.
/// </remarks>
internal static class WaylandKeyRepeat
{
    public static void Install()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            // Posted, so a repeat started by the key events that come with the focus change is caught too.
            window.Activated += (_, _) => Dispatcher.UIThread.Post(() => Stop(window), DispatcherPriority.Input);
            window.Deactivated += (_, _) => Stop(window);
        });
    }

    private static void Stop(Window window)
    {
        try
        {
            // The repeat lives on the window's current surface sink, not on the window itself.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var impl = window.PlatformImpl;
            var sink = impl?.GetType().GetProperty("CurrentSink", flags)?.GetValue(impl);
            sink?.GetType().GetMethod("StopKeyRepeat", flags, Type.EmptyTypes)?.Invoke(sink, null);
        }
        catch (Exception)
        {
            // Best effort: without it the shortcut may repeat, as before.
        }
    }
}
