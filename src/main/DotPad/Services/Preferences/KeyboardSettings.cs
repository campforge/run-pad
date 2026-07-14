namespace DotPad.Services.Preferences;

public sealed class KeyboardSettings
{
    public string Run { get; set; } = "F5";
    public string Cancel { get; set; } = "Escape";
    public string StepOver { get; set; } = "F10";
    public string StepIn { get; set; } = "F11";
    public string StepOut { get; set; } = "Shift+F11";
    public string Format { get; set; } = "Shift+Alt+F";
    public string GoToLine { get; set; } = "Ctrl+G";

    // TODO.md #80: these 4 used to be literal Key.XXX checks — MainWindow.axaml.cs (F4/Ctrl+W) and
    // FileTreeView.cs (F2/Delete) — sitting right next to the gestures above but not going through
    // this settings object at all, so they weren't shown as a tooltip/menu hint, weren't editable in
    // Preferences, and a third registered command would again mean editing the dispatcher directly.
    public string OpenDependencies { get; set; } = "F4";
    public string CloseTab { get; set; } = "Ctrl+W";
    public string RenameFile { get; set; } = "F2";
    public string DeleteFile { get; set; } = "Delete";
}
