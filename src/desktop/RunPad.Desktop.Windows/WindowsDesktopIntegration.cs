using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace RunPad.Desktop.Windows;

/// <summary>
/// RunPad on Windows: windows draw their own title bars inside the system's border. With a dark
/// theme the system's part of a window — its border, and the title bar of a window that keeps the
/// system's — is made dark as well, instead of staying light.
/// </summary>
public sealed class WindowsDesktopIntegration : IDesktopIntegration
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE: Windows 10 20H1 and later; older versions ignore it.
    private const int UseImmersiveDarkMode = 20;

    public IReadOnlyList<SystemSetting> Settings { get; } = [];

    public WindowFrame MainWindowFrame => WindowFrame.CustomTitleBar;

    public AppBuilder ConfigurePlatform(AppBuilder builder, ISystemSettingsReader settings)
        => builder.UsePlatformDetect();

    public void OnApplicationStarted(Application application)
    {
        if (!OperatingSystem.IsWindows()) return;
        var open = new List<Window>();
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            window.Opened += (_, _) =>
            {
                open.Add(window);
                ApplyTheme(window, application.ActualThemeVariant);
            };
            window.Closed += (_, _) => open.Remove(window);
        });
        application.ActualThemeVariantChanged += (_, _) =>
        {
            foreach (var window in open) ApplyTheme(window, application.ActualThemeVariant);
        };
    }

    private static void ApplyTheme(Window window, ThemeVariant theme)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle) return;
        var dark = theme == ThemeVariant.Dark ? 1 : 0;
        // Best effort: a failure leaves the system's light title bar.
        _ = DwmSetWindowAttribute(handle.Handle, UseImmersiveDarkMode, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
