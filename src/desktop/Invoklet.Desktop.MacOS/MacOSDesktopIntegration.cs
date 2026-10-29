using Avalonia;

namespace Invoklet.Desktop.MacOS;

/// <summary>Invoklet on macOS: AppKit's title bar and menu bar, and Control-click as the secondary click.</summary>
public sealed class MacOSDesktopIntegration : IDesktopIntegration
{
    public IReadOnlyList<SystemSetting> Settings { get; } = [];

    public WindowFrame MainWindowFrame => WindowFrame.MacOS;

    public AppBuilder ConfigurePlatform(AppBuilder builder, ISystemSettingsReader settings)
    {
        // Before AppKit starts: it reads these switches when it builds the menu bar.
        MacOSEditMenuItems.Disable();
        return builder.UsePlatformDetect()
            // The application menu stays as Invoklet builds it, without the Hide/Show All/Quit items
            // Avalonia appends: Quit is File → Exit, as on other systems.
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true });
    }

    public void OnApplicationStarted(Application application) => ControlClick.RegisterForApp();
}
