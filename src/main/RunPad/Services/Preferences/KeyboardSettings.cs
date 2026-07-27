namespace RunPad.Services.Preferences;

public sealed class KeyboardSettings
{
    public string Run { get; set; } = "F5";
    public string Cancel { get; set; } = "Escape";
    public string StepOver { get; set; } = "F10";
    public string StepIn { get; set; } = "F11";
    public string StepOut { get; set; } = "Shift+F11";
    public string Format { get; set; } = "Shift+Alt+F";
    public string GoToLine { get; set; } = "Ctrl+G";

    // Bug report 2026-07-17: there was no explicit Save action anywhere (only implicit saves on tab
    // close/Run/etc., or the always-dialog "Save As") — pressing Ctrl+S out of muscle memory matched
    // no gesture and silently did nothing, so IsDirty (correctly) never cleared. See
    // EditorTabsControl.SaveActive.
    public string Save { get; set; } = "Ctrl+S";

    // Bug report 2026-07-27: Ctrl+Shift+S ("Save As", the conventional gesture across editors) matched
    // no gesture and silently did nothing — same class of gap as Save above, just for the
    // always-shows-the-dialog variant (as opposed to Save, which only falls through to Save As for an
    // untitled tab). See EditorTabsControl.SaveAsActive.
    public string SaveAs { get; set; } = "Ctrl+Shift+S";

    // TODO.md #80: these 4 used to be literal Key.XXX checks — MainWindow.axaml.cs (F4/Ctrl+W) and
    // FileTreeView.cs (F2/Delete) — sitting right next to the gestures above but not going through
    // this settings object at all, so they weren't shown as a tooltip/menu hint, weren't editable in
    // Preferences, and a third registered command would again mean editing the dispatcher directly.
    public string OpenDependencies { get; set; } = "F4";
    public string CloseTab { get; set; } = "Ctrl+W";
    public string RenameFile { get; set; } = "F2";
    public string DeleteFile { get; set; } = "Delete";

    // TODO.md #114: there was no hotkey for "new file" at all — creating one meant reaching for the
    // File menu every time. Skips NewFileKindDialog entirely, using EditorSettings.NewFileLanguage/
    // NewFileKind instead (see MainWindow.OnWindowKeyDown).
    public string NewFile { get; set; } = "Ctrl+N";

    // Feature request 2026-07-27 (live user report) — ReSharper-style "add all missing usings" in one
    // shot, instead of accepting the lightbulb's "using X;" fix one at a time. See
    // EditorTabsControl.ResolveAllUsingsActive / EditorTabItemControl.ResolveAllUsingsAsync.
    public string ResolveAllUsings { get; set; } = "Ctrl+Alt+U";
}
