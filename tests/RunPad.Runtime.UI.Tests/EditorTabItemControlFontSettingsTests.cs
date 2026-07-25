using Avalonia.Headless.XUnit;
using Avalonia.Media;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #112 — live user report: the editor's font family is whatever the OS/theme default
// resolves to, ligature-capable fonts included (e.g. "=>" rendering as a single arrow glyph) — no
// setting to change it, because RunPad never asked for a specific font in the first place.
public class EditorTabItemControlFontSettingsTests
{
    // Confirmed via a live headless-Skia render probe (not guessed): Avalonia's comma-separated
    // FontFamily fallback list does NOT move on to the next name when the first one isn't installed —
    // on Linux, where neither "Consolas" nor "Menlo" exist, a chain starting with either of them
    // silently fell back to the surrounding theme's own (proportional) default, i.e. exactly the bug
    // this setting exists to fix. The bare "monospace" keyword is what actually rendered as a real,
    // non-ligature monospace font in that same probe — pinned here so a future "helpful" change back
    // to a named fallback chain doesn't quietly reintroduce the original bug.
    [Fact]
    public void EditorSettings_Should_DefaultFontFamily_ToTheBareGenericKeyword_NotANamedFallbackChain()
    {
        Assert.Equal("monospace", new EditorSettings().FontFamily);
    }

    // TODO.md #113 — live user report ("слишком крупный шрифт и отступы", LINQPad 4 as a denser
    // reference): 14 was an unexplained outlier default; 12 is what testing it live settled on.
    [Fact]
    public void EditorSettings_Should_DefaultFontSize_To12_NotTheRoomier14()
    {
        Assert.Equal(12, new EditorSettings().FontSize);
    }

    [AvaloniaFact]
    public void ApplyEditorSettings_Should_ApplyFontFamily_FromEditorSettings()
    {
        var editor = new CodeTextEditor();
        var settings = new EditorSettings { FontFamily = "Liberation Mono" };

        EditorsTabItemControl.ApplyEditorSettings(editor, settings);

        Assert.Equal(new FontFamily("Liberation Mono"), editor.FontFamily);
    }

    [AvaloniaFact]
    public void ApplyEditorSettings_Should_StillApplyFontSize_AlongsideFontFamily()
    {
        var editor = new CodeTextEditor();
        var settings = new EditorSettings { FontSize = 18, FontFamily = "monospace" };

        EditorsTabItemControl.ApplyEditorSettings(editor, settings);

        Assert.Equal(18, editor.FontSize);
        Assert.Equal(new FontFamily("monospace"), editor.FontFamily);
    }
}
