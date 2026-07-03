namespace DotPad.Services.Preferences;

public sealed class KeyboardSettings
{
    public string Run      { get; set; } = "F5";
    public string Cancel   { get; set; } = "Escape";
    public string StepOver { get; set; } = "F10";
    public string StepIn   { get; set; } = "F11";
    public string StepOut  { get; set; } = "Shift+F11";
}
