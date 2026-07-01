using DotPad.Models;

namespace DotPad.Services.Preferences;

public sealed class AppearanceSettings
{
    public ThemeMode Mode                { get; set; } = ThemeMode.System;
    public string    DarkInterfaceTheme  { get; set; } = "VS Code Dark";
    public string    LightInterfaceTheme { get; set; } = "VS Code Light";
    public string    DarkEditorTheme     { get; set; } = "Dark+";
    public string    LightEditorTheme    { get; set; } = "Light+";
}
