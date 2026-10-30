using Xunit;

namespace Invoklet.Runtime.Tests;

// Util.Highlight, HighlightIf, WithStyle and WithHeading dress a value in the output of Dump; they are values themselves, so they work alone
// and inside what is dumped.
public class UtilStylingTests
{
    private static string Html(object? value) => DumpRenderer.Render(value, null);

    [Fact]
    public void Highlight_APrimitive_IsASpanWithTheHighlightClassAroundTheValue()
    {
        var html = Html(Util.Highlight(42));

        Assert.Equal("<span class=\"dp-highlight\"><span class=\"dp-number\">42</span></span>", html);
    }

    [Fact]
    public void Highlight_AColour_IsTheBackgroundOfTheElement()
    {
        var html = Html(Util.Highlight("x", "lightgreen"));

        Assert.StartsWith("<span class=\"dp-highlight\" style=\"background-color:lightgreen\">", html);
    }

    [Fact]
    public void Highlight_ATable_IsADivBecauseASpanCannotHoldIt()
    {
        var html = Html(Util.Highlight(new[] { 1, 2 }));

        Assert.StartsWith("<div class=\"dp-highlight\">", html);
        Assert.EndsWith("</div>", html);
        Assert.Contains("<table", html);
    }

    [Fact]
    public void Highlight_Null_ShowsTheNullInsideTheHighlight()
    {
        var html = Html(Util.Highlight<string?>(null));

        Assert.Equal("<span class=\"dp-highlight\"><span class=\"dp-null\">null</span></span>", html);
    }

    [Fact]
    public void HighlightIf_ThePredicateIsTrue_Highlights()
    {
        var html = Html(Util.HighlightIf(3, n => n < 5));

        Assert.Contains("dp-highlight", html);
    }

    [Fact]
    public void HighlightIf_ThePredicateIsFalse_ReturnsTheValueUntouched()
    {
        var result = Util.HighlightIf(30, n => n < 5);

        Assert.Equal(30, result);
        Assert.DoesNotContain("dp-highlight", Html(result));
    }

    [Fact]
    public void HighlightIf_NoPredicate_Throws()
        => Assert.Throws<ArgumentNullException>(() => Util.HighlightIf(1, null!));

    [Fact]
    public void WithStyle_TheCss_IsTheStyleOfTheElement_AndIsEncoded()
    {
        var html = Html(Util.WithStyle("hi", "color:red; font-family:\"Arial\""));

        Assert.StartsWith("<span class=\"dp-styled\" style=\"color:red; font-family:&quot;Arial&quot;\">", html);
    }

    [Fact]
    public void WithStyle_NoCss_AddsNoStyleAttribute()
    {
        var html = Html(Util.WithStyle(1, "  "));

        Assert.DoesNotContain("style=", html);
    }

    [Fact]
    public void WithHeading_AStringHeading_IsPlainTextAboveTheValue()
    {
        var html = Html(Util.WithHeading(5, "Total <items>"));

        Assert.Equal(
            "<div class=\"dp-headed\"><div class=\"dp-heading\">Total &lt;items&gt;</div><span class=\"dp-number\">5</span></div>", html);
    }

    [Fact]
    public void WithHeading_AnObjectHeading_IsRenderedLikeAnyValue()
    {
        var html = Html(Util.WithHeading("v", 7));

        Assert.Contains("<div class=\"dp-heading\"><span class=\"dp-number\">7</span></div>", html);
    }

    [Fact]
    public void Wrappers_Nested_ApplyOutsideIn()
    {
        var html = Html(Util.WithHeading(Util.Highlight(1), "n"));

        Assert.Contains("<div class=\"dp-heading\">n</div><span class=\"dp-highlight\">", html);
    }

    [Fact]
    public void Wrappers_InsideAnObject_StyleTheCellOfThatProperty()
    {
        var html = Html(new { Name = "a", Qty = Util.HighlightIf(2, q => q < 5) });

        Assert.Contains("dp-highlight", html);
        Assert.Contains(">Qty<", html);
    }

    [Fact]
    public void Wrapping_DoesNotChangeHowDeepTheValueIsDumped()
    {
        var plain = Html(new { A = new { B = new { C = new { D = 1 } } } });
        var wrapped = Html(new { A = Util.WithStyle(new { B = new { C = new { D = 1 } } }, "color:red") });

        Assert.Contains("dp-truncated", plain);
        Assert.Contains("dp-truncated", wrapped);
        Assert.Equal(plain.Split(new[] { "dp-truncated" }, StringSplitOptions.None).Length, wrapped.Split(new[] { "dp-truncated" }, StringSplitOptions.None).Length);
    }

    [Fact]
    public void ToString_OfAWrapper_IsTheValueItself()
    {
        Assert.Equal("42", Util.Highlight(42).ToString());
        Assert.Equal("x", Util.WithHeading("x", "h").ToString());
    }

    [Fact]
    public void WithCssClass_AClassOfTheScript_IsTheClassOfTheElementAroundTheValue()
    {
        var html = Html(Util.WithCssClass("x", "my-class other_1"));

        Assert.StartsWith("<span class=\"my-class other_1\">", html);
    }

    [Theory]
    [InlineData("a\" onload=\"x")]
    [InlineData("")]
    [InlineData("  ")]
    public void WithCssClass_AClassThatCouldBreakTheAttribute_KeepsOnlyNameCharacters(string cssClass)
    {
        var html = Html(Util.WithCssClass("x", cssClass));

        Assert.DoesNotContain("\"", html.Split('>')[0].Replace("class=\"", "").TrimEnd('"'));
        Assert.DoesNotContain("onload=", html);
    }

    [Fact]
    public void AddStyles_Rules_AreWrittenAsADumpLineWithAStyleElementThatCannotCloseItself()
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            Util.HtmlHead.AddStyles(".a { color: red } </style><b>");
        }
        finally
        {
            Console.SetOut(original);
        }

        var line = writer.ToString().TrimEnd();
        Assert.Equal(ObjectExtensions.DumpLinePrefixChar, line[0]);
        var html = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line[1..]));
        Assert.StartsWith("<style>.a { color: red }", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "</style"));
    }
}
