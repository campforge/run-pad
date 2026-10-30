using Avalonia;

namespace Invoklet.Desktop;

/// <summary>
/// What Invoklet does differently on one operating system's desktop: how Avalonia starts there, how
/// the main window is framed, and the system settings Preferences shows for it. Each OS's build of
/// Invoklet carries the one integration for that OS.
/// </summary>
public interface IDesktopIntegration
{
    /// <summary>The settings shown in Preferences → Interface → System; empty hides that page.</summary>
    IReadOnlyList<SystemSetting> Settings { get; }

    /// <summary>
    /// How the main window is framed. Known only after <see cref="ConfigurePlatform"/> where it
    /// depends on the platform chosen there.
    /// </summary>
    WindowFrame MainWindowFrame { get; }

    /// <summary>
    /// Picks and configures the Avalonia platform, before Avalonia starts; <paramref name="settings"/>
    /// are the values of <see cref="Settings"/> as last saved.
    /// </summary>
    AppBuilder ConfigurePlatform(AppBuilder builder, ISystemSettingsReader settings);

    /// <summary>Once the application exists and before its first window: app-wide handlers and resources.</summary>
    void OnApplicationStarted(Application application);
}

/// <summary>The main window's frame and title bar.</summary>
public enum WindowFrame
{
    /// <summary>Invoklet's own title row and window buttons inside a system-drawn border (Windows).</summary>
    CustomTitleBar,

    /// <summary>The system's own title bar above Invoklet's menu row.</summary>
    SystemTitleBar,

    /// <summary>Avalonia draws frame and shadow around every window; Invoklet's own title row (native Wayland).</summary>
    DrawnFrame,

    /// <summary>The macOS title bar with its traffic-light buttons, Invoklet's toolbar beside them.</summary>
    MacOS,
}

/// <summary>Saved values of system settings, readable before Avalonia and Invoklet's services start.</summary>
public interface ISystemSettingsReader
{
    bool Get(SystemSetting setting);
}
