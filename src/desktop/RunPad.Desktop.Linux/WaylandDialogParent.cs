using System.Reflection;
using Avalonia.Controls;

namespace RunPad.Desktop.Linux;

/// <summary>
/// Tells the compositor which window a dialog belongs to, so it is placed over that window.
/// </summary>
/// <remarks>
/// Wayland apps cannot position their windows; the compositor places a dialog over its parent when
/// it knows the parent. Avalonia's Wayland back end passes the owner on only if the window's surface
/// already exists, but sets it before showing the window, when it does not, so the compositor never
/// learnt it. It is set again once the window is shown. Compositors that place a window when it first
/// draws (GNOME) then centre the dialog on its owner and keep it above it; KWin places a window
/// earlier, so on KDE the window rules in KdeWindowRule centre dialogs instead.
/// </remarks>
internal static class WaylandDialogParent
{
    public static void Install()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
            window.Opened += (_, _) =>
            {
                if (window.Owner is Window { PlatformImpl: { } owner } && window.PlatformImpl is { } impl)
                    SetParent(impl, owner);
            });
    }

    // IWindowImpl.SetParent is internal to Avalonia; the Wayland window class has it as a public method.
    private static void SetParent(object impl, object owner)
    {
        try
        {
            impl.GetType().GetMethod("SetParent", BindingFlags.Instance | BindingFlags.Public)?.Invoke(impl, [owner]);
        }
        catch (Exception)
        {
            // Best effort: the dialog is then placed like any other window.
        }
    }
}
