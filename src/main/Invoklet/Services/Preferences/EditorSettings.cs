using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Format.RPad;
using Invoklet.Services.Scaling;

namespace Invoklet.Services.Preferences;

public sealed class EditorSettings
{
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowFolding { get; set; } = true;
    public bool UseTabs { get; set; }
    public int IndentationSize
    {
        get;
        set => field = Math.Clamp(value, 1, 16);
    } = 4;

    /// <summary>One level of indentation as these settings type it: a tab, or the width in spaces.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string IndentationUnit => UseTabs ? "\t" : new string(' ', IndentationSize);

    public bool ShowTabs { get; set; }
    public bool ShowSpaces { get; set; }
    public bool ShowEndOfLine { get; set; }
    public bool ShowColumnRuler { get; set; }
    public int ColumnRulerPosition { get; set; } = 80;
    public bool WordWrap { get; set; }

    // Wraps long lines in the results pane. On by default so long output is readable at once;
    // settings files written before this existed simply get the default.
    public bool ResultsWordWrap { get; set; } = true;

    // Live user report ("font and spacing are too big", LINQPad 4 shown as a
    // denser reference) — 14 was the one outlier default nobody had touched; 12 is what testing it
    // live actually settled on.
    public double FontSize { get; set; } = 12;

    /// <summary>
    /// Extra zoom of code in percent, on top of the interface scale (100 = the interface-scaled
    /// <see cref="FontSize"/>). Shared by every editor and kept across restarts.
    /// </summary>
    public double ZoomPercent
    {
        get;
        set => field = ScalePercent.NormalizeCodeZoom(value);
    } = ScalePercent.Default;

    // The code editor never set FontFamily at all before this — it inherited whatever
    // FluentTheme's own default font resolved to for the current OS, entirely outside Invoklet's
    // control. Live user report: on at least one such default, ligature-capable glyphs (e.g. "=>"
    // rendering as a single arrow) got substituted in — not something Invoklet ever asked for, and with
    // no setting to turn it off since there was nothing to turn off in the first place.
    //
    // "monospace" stands for the platform's default code font, not a font name: the editor shows it
    // in MonospaceFont.Default, a fixed-pitch font that comes with the system (Menlo on macOS,
    // Cascadia Mono or Consolas on Windows, DejaVu Sans Mono or similar on Linux). Kept generic
    // rather than saving the font found on this machine, so a settings file moved to another
    // system still gets that system's font. Those system fonts don't use ligatures (an opt-in
    // feature of programming fonts such as Fira Code or Cascadia Code), so "=>" stays two
    // characters unless the user picks such a font here.
    public string FontFamily { get; set; } = MonospaceFont.GenericName;

    // Global switch for "typing punctuation also commits the open completion
    // window" — some users find that unpredictable and want completions accepted only via
    // Enter/Tab/click.
    public bool CommitCharactersEnabled { get; set; } = true;

    // File → New RPad File always showed NewFileKindDialog to pick a RpadKind, even for
    // a user who creates the same language+kind combination every time — these are the defaults that
    // Ctrl+N (MainWindow.OnWindowKeyDown) and the new "New File" menu item use to skip that dialog
    // entirely. The explicit File → New RPad File → C#/Visual Basic submenu and the file tree's own
    // "New File" context menu are unaffected — both still ask explicitly.
    public RpadLanguage NewFileLanguage { get; set; } = RpadLanguage.CSharp;
    public RpadKind NewFileKind { get; set; } = RpadKind.Statements;
}
