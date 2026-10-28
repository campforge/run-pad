namespace RunPad.Services.Desktop;

/// <summary>
/// What the desktop RunPad runs on offers beyond plain Avalonia: today Linux's native Wayland back end
/// and KDE window rules. Off Linux, or in a build without the Linux integration, everything is off.
/// </summary>
public interface IDesktopSession
{
    /// <summary>The native Wayland back end could be used here (Preferences shows its switch).</summary>
    bool NativeWaylandAvailable { get; }

    /// <summary>RunPad runs on native Wayland and Avalonia draws the window frames.</summary>
    bool NativeWaylandActive { get; }

    /// <summary>KDE can be asked to remember the window position (Preferences shows its switch).</summary>
    bool CanRememberWindowPosition { get; }

    /// <summary>Whether KDE's window rule for RunPad exists; setting it adds or removes the rule.</summary>
    bool RememberWindowPosition { get; set; }
}
