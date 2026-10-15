using Avalonia.Headless.XUnit;
using Avalonia.Media;
using RunPad.Avalonia.CodeTextEditor;
using RunPad.Services.Preferences;
using RunPad.UserControls;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Live user report: the editor's font family is whatever the OS/theme default
// resolves to, ligature-capable fonts included (e.g. "=>" rendering as a single arrow glyph) — no
// setting to change it, because RunPad never asked for a specific font in the first place.
public class EditorTabItemControlFontSettingsTests
{
    // The setting keeps the generic name ("the platform's default code font"), not this machine's
    // font name, so a settings file moved to another system still gets that system's font.
    [Fact]
    public void EditorSettings_Should_DefaultFontFamily_ToTheGenericName()
    {
        Assert.Equal(MonospaceFont.GenericName, new EditorSettings().FontFamily);
    }

    // Live user report ("font size and padding too large", LINQPad 4 as a denser
    // reference): 14 was an unexplained outlier default; 12 is what testing it live settled on.
    [Fact]
    public void EditorSettings_Should_DefaultFontSize_To12_NotTheRoomier14()
    {
        Assert.Equal(12, new EditorSettings().FontSize);
    }

    // The generic name used to reach the editor as is — Helvetica on macOS.
    [AvaloniaFact]
    public void ApplyEditorSettings_Should_ShowTheGenericSetting_InThePlatformsDefaultCodeFont()
    {
        var editor = new CodeTextEditor();

        EditorsTabItemControl.ApplyEditorSettings(editor, new EditorSettings { FontFamily = "monospace" });

        Assert.Equal(MonospaceFont.Default, editor.FontFamily);
    }

    // A named font that isn't installed falls back to the default code font, not the theme's
    // proportional font; the setting itself is left as the user wrote it.
    [AvaloniaFact]
    public void ApplyEditorSettings_Should_UseTheDefaultCodeFont_ForANamedFontThatIsNotInstalled()
    {
        var editor = new CodeTextEditor();
        var settings = new EditorSettings { FontFamily = "No Such Font 7f3c" };

        EditorsTabItemControl.ApplyEditorSettings(editor, settings);

        Assert.Equal(MonospaceFont.Default, editor.FontFamily);
        Assert.Equal("No Such Font 7f3c", settings.FontFamily);
    }

    [AvaloniaFact]
    public void ApplyEditorSettings_Should_StillApplyFontSize_AlongsideFontFamily()
    {
        var editor = new CodeTextEditor();
        var settings = new EditorSettings { FontSize = 18, FontFamily = "monospace" };

        EditorsTabItemControl.ApplyEditorSettings(editor, settings);

        Assert.Equal(18, editor.FontSize);
        Assert.Equal(MonospaceFont.Default, editor.FontFamily);
    }
}
