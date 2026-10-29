using Avalonia;
using Invoklet.Services.Preferences;
using Invoklet.Desktop;

namespace Invoklet.Services.Desktop;

/// <summary>
/// The desktop integration this build carries: Invoklet.csproj references the one for the OS it is
/// built for (Invoklet_LINUX, Invoklet_MACOS, Invoklet_WINDOWS). <see cref="Configure"/> runs it before
/// Avalonia starts; the same instance is then registered in DI as <see cref="IDesktopIntegration"/>.
/// </summary>
internal static class DesktopIntegration
{
    public static IDesktopIntegration Current { get; } =
#if Invoklet_LINUX
        new Invoklet.Desktop.Linux.LinuxDesktopIntegration();
#elif Invoklet_MACOS
        new Invoklet.Desktop.MacOS.MacOSDesktopIntegration();
#elif Invoklet_WINDOWS
        new Invoklet.Desktop.Windows.WindowsDesktopIntegration();
#else
        new PlainDesktopIntegration();
#endif

    public static AppBuilder Configure(AppBuilder builder)
        => Current.ConfigurePlatform(builder, new SavedSettings(PreferencesService.ReadForStartup().System));

    private sealed class SavedSettings(IReadOnlyDictionary<string, bool> saved) : ISystemSettingsReader
    {
        public bool Get(SystemSetting setting) => saved.GetValueOrDefault(setting.Id, setting.DefaultValue);
    }

    // A build for a system without an integration of its own.
    private sealed class PlainDesktopIntegration : IDesktopIntegration
    {
        public IReadOnlyList<SystemSetting> Settings { get; } = [];

        public WindowFrame MainWindowFrame => WindowFrame.CustomTitleBar;

        public AppBuilder ConfigurePlatform(AppBuilder builder, ISystemSettingsReader settings)
            => builder.UsePlatformDetect();

        public void OnApplicationStarted(Application application) { }
    }
}
