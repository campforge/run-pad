using Avalonia.Styling;
using RunPad.Avalonia.Themes;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class HtmlThemeStylesheetTests
{
    [Fact]
    public void Wrapping_Should_BeOffByDefault_AndLeaveTheStylesheetUnchanged()
    {
        var plain = HtmlThemeStylesheet.Generate(ThemeVariant.Dark, isDark: true);

        Assert.Equal(plain, HtmlThemeStylesheet.Generate(ThemeVariant.Dark, isDark: true, wrapLongLines: false));
        Assert.DoesNotContain("pre-wrap", plain);
        Assert.DoesNotContain("break-all", plain);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wrapping_Should_OnlyAffectPreformattedOutput(bool isDark)
    {
        var variant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        var wrapped = HtmlThemeStylesheet.Generate(variant, isDark, wrapLongLines: true);
        var plain = HtmlThemeStylesheet.Generate(variant, isDark, wrapLongLines: false);

        var wrappedPre = wrapped.Split('\n').Single(l => l.TrimStart().StartsWith("pre "));
        Assert.Contains("white-space:pre-wrap", wrappedPre);
        Assert.Contains("word-break:break-all", wrappedPre);
        Assert.Equal(wrapped.Replace(" white-space:pre-wrap; word-break:break-all;", ""), plain);
    }
}
