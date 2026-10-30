using AvaloniaEdit.TextMate;
using TextMateSharp.Themes;
using Xunit;
using AM = Avalonia.Media;

namespace Invoklet.NonUI.Tests;

// Live user report: comments rendered in italic ("color alone is enough for them to
// stand out"). Bundled TextMateSharp themes (ThemeService.cs's ThemeName.* set,
// including the default Dark+) mark the comment scope's tokenColors fontStyle as italic, mirroring
// VS Code's own built-in themes — TextTransformation.GetFontStyle() used to translate that straight
// into Avalonia.Media.FontStyle.Italic. Now always Normal, regardless of what the active theme says.
public class TextTransformationFontStyleTests
{
    [Fact]
    public void GetFontStyle_Should_BeNormal_EvenWhenTheThemeMarksTheScopeItalic()
    {
        var transformation = new ForegroundTextTransformation { FontStyle = FontStyle.Italic };

        Assert.Equal(AM.FontStyle.Normal, transformation.GetFontStyle());
    }

    [Fact]
    public void GetFontStyle_Should_BeNormal_WhenTheThemeDoesNotSetAnyFontStyle()
    {
        var transformation = new ForegroundTextTransformation { FontStyle = FontStyle.NotSet };

        Assert.Equal(AM.FontStyle.Normal, transformation.GetFontStyle());
    }

    // Bold/underline are untouched by this fix — only italic was ever the complaint, and both are
    // handled by separate methods (GetFontWeight/IsUnderline) that GetFontStyle doesn't call into.
    [Fact]
    public void GetFontStyle_Should_BeNormal_EvenWhenTheThemeCombinesItalicWithBold()
    {
        var transformation = new ForegroundTextTransformation { FontStyle = FontStyle.Italic | FontStyle.Bold };

        Assert.Equal(AM.FontStyle.Normal, transformation.GetFontStyle());
    }
}
