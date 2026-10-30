using Avalonia;
using Avalonia.Media;

namespace Invoklet.Desktop.Linux;

/// <summary>Invoklet on a Linux desktop: native Wayland with drawn frames when it can, and KDE window rules.</summary>
public sealed class LinuxDesktopIntegration : IDesktopIntegration
{
    // Matches Invoklet.desktop, so the desktop pairs the window with its launcher and window rules.
    private const string AppId = "Invoklet";

    public static readonly SystemSetting NativeWaylandSetting = new()
    {
        Id = "linux.nativeWayland",
        Title = "Native Wayland window",
        Description = "Invoklet draws its own window frames on Wayland. Turn off if a window misbehaves to use "
            + "XWayland and the system frame. INVOKLET_WAYLAND=0 also turns it off, for when Invoklet cannot start.",
        DefaultValue = true,
        RequiresRestart = true,
        IsAvailable = () => NativeWayland.IsAvailable,
    };

    public static readonly SystemSetting KdeWindowRulesSetting = new()
    {
        Id = "linux.kdeWindowRules",
        Title = "Remember window position and center dialogs",
        Description = "Adds two window rules for Invoklet to KDE's settings (System Settings → Window Rules): KDE "
            + "puts the main window back where it was and opens dialogs in the middle of the screen. On Wayland "
            + "only KDE can place windows. Turning it off removes the rules.",
        IsAvailable = () => KdeWindowRule.IsSupported,
        External = new ExternalValue(KdeWindowRule.IsInstalled, KdeWindowRule.SetInstalled),
    };

    public IReadOnlyList<SystemSetting> Settings { get; } = [NativeWaylandSetting, KdeWindowRulesSetting];

    public WindowFrame MainWindowFrame { get; private set; } = WindowFrame.SystemTitleBar;

    public AppBuilder ConfigurePlatform(AppBuilder builder, ISystemSettingsReader settings)
    {
        KdeWindowRule.UpdateIfInstalled();
        if (NativeWayland.IsAvailable
            && Environment.GetEnvironmentVariable("INVOKLET_WAYLAND") is not "0"
            && settings.Get(NativeWaylandSetting))
        {
            MainWindowFrame = WindowFrame.DrawnFrame;
            return builder.UseNativeWayland(AppId);
        }
        // XWayland or X11: a request for a client-drawn frame comes back as a bare rectangle with no
        // border or shadow, so the system draws the frame.
        MainWindowFrame = WindowFrame.SystemTitleBar;
        return builder.UsePlatformDetect();
    }

    public void OnApplicationStarted(Application application)
    {
        // Fluent puts its bundled Inter first; use the desktop's own UI font instead, as VS Code does.
        // $Default is fontconfig's default sans face (Noto Sans on KDE, for one).
        application.Resources["ContentControlThemeFontFamily"] = new FontFamily("$Default");
    }
}
