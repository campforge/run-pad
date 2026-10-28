using Avalonia;
using RunPad.Services.Preferences;
#if RUNPAD_LINUX
using RunPad.Desktop.Linux;
#endif

namespace RunPad.Services.Desktop;

/// <summary>
/// The desktop integration, chosen once in <see cref="Configure"/> before Avalonia starts and then
/// handed to the rest of RunPad through DI as <see cref="Current"/>. Only Linux builds reference
/// RunPad.Desktop.Linux; elsewhere this compiles to "nothing available".
/// </summary>
internal sealed class DesktopSession : IDesktopSession
{
    // Matches runpad.desktop.
    private const string AppId = "runpad";

    private DesktopSession(bool nativeWaylandActive) => NativeWaylandActive = nativeWaylandActive;

    /// <summary>The session <see cref="Configure"/> chose; plain Avalonia until it runs (tests, designer).</summary>
    public static DesktopSession Current { get; private set; } = new(nativeWaylandActive: false);

    /// <summary>
    /// Picks the Avalonia platform. Native Wayland needs a Linux build, a Wayland session and the user
    /// not to have turned it off — in Preferences or, when RunPad cannot start at all, with RUNPAD_WAYLAND=0.
    /// </summary>
    public static AppBuilder Configure(AppBuilder builder)
    {
#if RUNPAD_LINUX
        KdeWindowRule.UpdateIfInstalled();
        if (NativeWayland.IsAvailable
            && Environment.GetEnvironmentVariable("RUNPAD_WAYLAND") is not "0"
            && PreferencesService.ReadForStartup().Appearance.NativeWayland)
        {
            Current = new DesktopSession(nativeWaylandActive: true);
            return builder.UseNativeWayland(AppId);
        }
#endif
        Current = new DesktopSession(nativeWaylandActive: false);
        return builder.UsePlatformDetect();
    }

    public bool NativeWaylandActive { get; }

#if RUNPAD_LINUX
    public bool NativeWaylandAvailable => NativeWayland.IsAvailable;

    public bool CanRememberWindowPosition => KdeWindowRule.IsSupported;

    public bool RememberWindowPosition
    {
        get => KdeWindowRule.IsInstalled();
        set => KdeWindowRule.SetInstalled(value);
    }
#else
    public bool NativeWaylandAvailable => false;

    public bool CanRememberWindowPosition => false;

    public bool RememberWindowPosition
    {
        get => false;
        set { }
    }
#endif
}
