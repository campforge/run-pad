namespace DotPad.Services.Preferences;

public interface IPreferencesService
{
    AppearanceSettings Appearance { get; }
    EditorSettings Editor { get; }
    DebuggerSettings Debugger { get; }
    KeyboardSettings Keyboard { get; }

    void Save();

    event Action? Changed;
}
