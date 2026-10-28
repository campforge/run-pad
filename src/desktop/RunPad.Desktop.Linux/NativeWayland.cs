using Avalonia;

namespace RunPad.Desktop.Linux;

/// <summary>
/// Avalonia's native Wayland back end, with the window frame drawn by the app, and the fixes it needs.
/// </summary>
public static class NativeWayland
{
    /// <summary>Running in a Wayland session: UseWayland has no fallback without a compositor.</summary>
    public static bool IsAvailable =>
        OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>
    /// Starts Avalonia on the native Wayland back end. <paramref name="appId"/> should match the app's
    /// .desktop file, so the desktop pairs the window with its launcher and window rules.
    /// </summary>
    public static AppBuilder UseNativeWayland(this AppBuilder builder, string appId)
    {
        SystemCursorTheme.Apply();

        // UseWayland covers windowing only; UsePlatformDetect would also bring Skia and HarfBuzz.
        // ForceDrawnDecorations: KWin would otherwise draw its own frame over the app's.
        return builder.UseWayland().UseSkia().UseHarfBuzz()
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
            .With(new WaylandPlatformOptions { ForceDrawnDecorations = true, AppId = appId })
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
            .AfterSetup(_ =>
            {
                WaylandKeyRepeat.Install();
                WaylandDialogParent.Install();
            });
    }
}
