namespace DotPad.Services.Preferences;

public interface IPreferencesService
{
    AppearanceSettings Appearance { get; }
    EditorSettings Editor { get; }
    DebuggerSettings Debugger { get; }
    CSharpLanguageSettings CSharp { get; }
    KeyboardSettings Keyboard { get; }
    LayoutSettings Layout { get; }
    SessionSettings Session { get; }

    void Save();

    event Action? Changed;
}
