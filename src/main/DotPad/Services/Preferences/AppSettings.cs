namespace DotPad.Services.Preferences;

public sealed class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public DebuggerSettings Debugger { get; set; } = new();
}
