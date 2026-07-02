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
}
