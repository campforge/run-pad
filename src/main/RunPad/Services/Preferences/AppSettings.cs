namespace DotPad.Services.Preferences;

public sealed class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public DebuggerSettings Debugger { get; set; } = new();
    public CSharpLanguageSettings CSharp { get; set; } = new();
    public KeyboardSettings Keyboard { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public SessionSettings Session { get; set; } = new();
}
