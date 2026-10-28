using RunPad.Avalonia.Themes;
using RunPad.Services.Scaling;

namespace RunPad.Services.Preferences;

public sealed class AppearanceSettings
{
    public bool RestoreUnsavedTabs { get; set; } = true;
    public ThemeMode Mode { get; set; } = ThemeMode.System;
    public string DarkInterfaceTheme { get; set; } = "VS Code Dark";
    public string LightInterfaceTheme { get; set; } = "VS Code Light";
    public string DarkEditorTheme { get; set; } = "Dark+";
    public string LightEditorTheme { get; set; } = "Light+";

    /// <summary>
    /// Linux only: run on the native Wayland back end with the window frame RunPad draws itself.
    /// Off falls back to XWayland and the system frame. Read once at startup.
    /// </summary>
    public bool NativeWayland { get; set; } = true;

    /// <summary>
    /// Size of the whole interface, code included, in percent (100 = as designed). Missing in older
    /// settings files, which therefore load at 100%.
    /// </summary>
    public double InterfaceScalePercent
    {
        get;
        set => field = ScalePercent.NormalizeInterface(value);
    } = ScalePercent.Default;
}
