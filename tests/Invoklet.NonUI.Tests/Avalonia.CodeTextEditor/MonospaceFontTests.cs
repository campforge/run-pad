using System.Text.Json;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Services.Preferences;
using Xunit;
using Platform = Invoklet.Avalonia.CodeTextEditor.MonospaceFont.Platform;

namespace Invoklet.NonUI.Tests;

// The default code font used to be the generic "monospace" name, which Avalonia resolves to
// Helvetica — a proportional face — on macOS. The default is now the first installed fixed-pitch
// font of a per-platform list; these tests drive that choice with stand-in font availability, so
// they hold on any machine (no Windows font is assumed to exist on macOS, or the reverse).
public class MonospaceFontTests
{
    [Theory]
    [InlineData(nameof(Platform.MacOS), new[] { "Menlo", "Monaco", "Courier New" })]
    [InlineData(nameof(Platform.Windows), new[] { "Cascadia Mono", "Consolas", "Courier New" })]
    [InlineData(nameof(Platform.Linux), new[] { "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Ubuntu Mono" })]
    public void CandidatesFor_Should_ListThePlatformsOwnCodeFonts_MostPreferredFirst(string platform, string[] expected)
    {
        Assert.Equal(expected, MonospaceFont.CandidatesFor(Enum.Parse<Platform>(platform)));
    }

    [Fact]
    public void SelectDefaultName_Should_TakeTheFirstInstalledFixedPitchCandidate()
    {
        HashSet<string> available = ["Monaco", "Courier New"];

        Assert.Equal("Monaco", MonospaceFont.SelectDefaultName(MonospaceFont.CandidatesFor(Platform.MacOS), available.Contains));
    }

    [Fact]
    public void SelectDefaultName_Should_PreferCascadiaMono_ButFallBackToConsolas_OnAnOlderWindows()
    {
        var windows = MonospaceFont.CandidatesFor(Platform.Windows);

        Assert.Equal("Cascadia Mono", MonospaceFont.SelectDefaultName(windows, _ => true));
        Assert.Equal("Consolas", MonospaceFont.SelectDefaultName(windows, n => n is "Consolas" or "Courier New"));
    }

    // Nothing from the list is there (a minimal Linux, a headless test host): the generic name is the
    // last resort — fontconfig maps it to a monospace face on Linux.
    [Fact]
    public void SelectDefaultName_Should_FallBackToTheGenericName_WhenNoCandidateIsAvailable()
    {
        Assert.Equal(MonospaceFont.GenericName, MonospaceFont.SelectDefaultName(MonospaceFont.CandidatesFor(Platform.Linux), _ => false));
    }

    // The generic name, or no value, is what settings written before this change hold: it now means
    // "the platform default" and is never replaced by this machine's font name.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("monospace")]
    [InlineData("Monospace")]
    public void ResolveName_Should_UseTheDefault_ForTheGenericNameOrNoValue(string? setting)
    {
        Assert.Null(MonospaceFont.ResolveName(setting, _ => true));
    }

    // An explicit choice is kept — even a proportional font, since the user picked it.
    [Theory]
    [InlineData("Fira Code", "Fira Code")]
    [InlineData("  JetBrains Mono ", "JetBrains Mono")]
    [InlineData("Helvetica", "Helvetica")]
    public void ResolveName_Should_KeepAnExplicitInstalledFont(string setting, string expected)
    {
        Assert.Equal(expected, MonospaceFont.ResolveName(setting, name => name == expected));
    }

    // A named font that isn't installed here (settings copied from another machine, a typo, an
    // uninstalled font) gets the platform default instead of the theme's proportional font.
    [Fact]
    public void ResolveName_Should_UseTheDefault_ForAnExplicitFontThatIsNotInstalled()
    {
        Assert.Null(MonospaceFont.ResolveName("Consolas", _ => false));
    }

    [Fact]
    public void EditorSettings_Should_KeepTheGenericName_AsDefault_AndAcrossSaveAndLoad()
    {
        Assert.Equal(MonospaceFont.GenericName, new EditorSettings().FontFamily);
        Assert.Equal(MonospaceFont.GenericName, JsonSerializer.Deserialize<EditorSettings>("{}")!.FontFamily);

        var explicitChoice = JsonSerializer.Deserialize<EditorSettings>(
            JsonSerializer.Serialize(new EditorSettings { FontFamily = "Fira Code", FontSize = 15 }))!;
        Assert.Equal("Fira Code", explicitChoice.FontFamily);
        Assert.Equal(15, explicitChoice.FontSize);
    }
}
