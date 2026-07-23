using DotPad.Format.RPad;

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

    // TODO.md #112: the code editor never set FontFamily at all before this — it inherited whatever
    // FluentTheme's own default font resolved to for the current OS, entirely outside DotPad's
    // control. Live user report: on at least one such default, ligature-capable glyphs (e.g. "=>"
    // rendering as a single arrow) got substituted in — not something DotPad ever asked for, and with
    // no setting to turn it off since there was nothing to turn off in the first place.
    //
    // "monospace" (the bare CSS-style generic keyword, not a real font name) rather than a named
    // fallback chain like "Consolas,Menlo,Courier New,monospace" — confirmed via a live headless-Skia
    // render probe that Avalonia's comma-separated FontFamily list does NOT cascade to the next name
    // when the first one isn't installed (only used for *glyph-level* fallback within whatever font
    // DID resolve): on Linux, where neither "Consolas" nor "Menlo" exist, that whole chain silently
    // fell back to FluentTheme's own (proportional, non-monospace) default — the exact bug this is
    // fixing, just reintroduced by the "fix" itself. The bare generic keyword is resolved specially by
    // Avalonia's own font manager on every platform and reliably returned a real, non-ligature
    // monospace font in that same probe. Real system monospace fonts essentially never enable
    // ligatures by default (that's an opt-in feature of specific "programming" fonts like Fira Code/
    // Cascadia Code/JetBrains Mono, not a general monospace-font trait), so this sidesteps the
    // reported bug without needing to name (and keep working, per-platform) specific font families.
    public string FontFamily { get; set; } = "monospace";

    // TODO.md #60.4: global switch for "typing punctuation also commits the open completion
    // window" — some users find that unpredictable and want completions accepted only via
    // Enter/Tab/click.
    public bool CommitCharactersEnabled { get; set; } = true;

    // TODO.md #114: File → New RPad File always showed NewFileKindDialog to pick a RpadKind, even for
    // a user who creates the same language+kind combination every time — these are the defaults that
    // Ctrl+N (MainWindow.OnWindowKeyDown) and the new "New File" menu item use to skip that dialog
    // entirely. The explicit File → New RPad File → C#/Visual Basic submenu and the file tree's own
    // "New File" context menu (TODO.md #109) are unaffected — both still ask explicitly.
    public RpadLanguage NewFileLanguage { get; set; } = RpadLanguage.CSharp;
    public RpadKind NewFileKind { get; set; } = RpadKind.Statements;
}
