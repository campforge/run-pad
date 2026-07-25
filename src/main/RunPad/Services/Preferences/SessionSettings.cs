namespace DotPad.Services.Preferences;

public sealed class SessionSettings
{
    public List<string> OpenTabs { get; set; } = [];
    public string? ActiveTabPath { get; set; }
}
