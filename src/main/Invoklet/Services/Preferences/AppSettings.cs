namespace Invoklet.Services.Preferences;

public sealed class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public DebuggerSettings Debugger { get; set; } = new();
    public CSharpLanguageSettings CSharp { get; set; } = new();
    public KeyboardSettings Keyboard { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public SessionSettings Session { get; set; } = new();
    public ExperimentalSettings Experimental { get; set; } = new();

    /// <summary>
    /// Settings the OS's desktop integration offers, by id ("linux.nativeWayland"); a missing one has
    /// its default. Kept as plain values so a settings file moved to another OS keeps them unread.
    /// </summary>
    public Dictionary<string, bool> System { get; set; } = [];
}
