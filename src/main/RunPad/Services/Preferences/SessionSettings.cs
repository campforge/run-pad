namespace RunPad.Services.Preferences;

public sealed class SessionSettings
{
    public List<string> RecentFiles { get; set; } = [];
    public List<string> OpenTabs { get; set; } = [];
    public string? ActiveTabPath { get; set; }
}
