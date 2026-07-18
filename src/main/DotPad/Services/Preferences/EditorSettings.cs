namespace DotPad.Services.Preferences;

public sealed class EditorSettings
{
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowFolding { get; set; } = true;
    public bool ShowTabs { get; set; }
    public bool ShowSpaces { get; set; }
    public bool ShowEndOfLine { get; set; }
    public bool ShowColumnRuler { get; set; }
    public int ColumnRulerPosition { get; set; } = 80;
    public bool WordWrap { get; set; }
    public double FontSize { get; set; } = 14;

    // TODO.md #60.4: global switch for "typing punctuation also commits the open completion
    // window" — some users find that unpredictable and want completions accepted only via
    // Enter/Tab/click.
    public bool CommitCharactersEnabled { get; set; } = true;
}
