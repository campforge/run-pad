using Avalonia.Styling;
using Invoklet.Avalonia.Themes;
using Xunit;

namespace Invoklet.NonUI.Tests.Avalonia.Themes;

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
    public void Wrapping_Should_OnlyAffectPreformattedTextAndOutputLines(bool isDark)
    {
        var variant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        var wrapped = HtmlThemeStylesheet.Generate(variant, isDark, wrapLongLines: true);
        var plain = HtmlThemeStylesheet.Generate(variant, isDark, wrapLongLines: false);
        string[] wrapping = ["pre", ".out", ".error"];
        string Rule(string css, string selector) => css.Split('\n').Single(l => l.TrimStart().StartsWith(selector + " "));

        foreach (var selector in wrapping)
        {
            Assert.Contains("white-space:pre-wrap", Rule(wrapped, selector));
            Assert.Contains("word-break:break-all", Rule(wrapped, selector));
            Assert.DoesNotContain("pre-wrap", Rule(plain, selector));
        }
        // Without wrapping, a script's output line keeps its spaces and a long token isn't broken.
        Assert.Contains("white-space:pre;", Rule(plain, ".out"));
        Assert.Contains("white-space:pre;", Rule(plain, ".error"));

        // Every other rule — Dump tables included — is the same either way.
        string Others(string css) => string.Join('\n', css.Split('\n').Where(l => !wrapping.Any(s => l.TrimStart().StartsWith(s + " "))));
        Assert.Equal(Others(plain), Others(wrapped));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheStylesheet_Should_StyleHighlightsAsAMarkerAndHeadingsAsBold_WithoutATheme(bool isDark)
    {
        var css = HtmlThemeStylesheet.Generate(isDark ? ThemeVariant.Dark : ThemeVariant.Light, isDark);

        var highlight = css.Split('\n').Single(l => l.TrimStart().StartsWith(".dp-highlight "));
        var heading = css.Split('\n').Single(l => l.TrimStart().StartsWith(".dp-heading "));
        Assert.Contains("background-color:#", highlight);
        Assert.Contains("color:#", highlight);
        Assert.Contains("font-weight:bold", heading);
    }
}
