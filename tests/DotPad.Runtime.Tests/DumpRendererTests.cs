using DotPad.Runtime;
using Xunit;

namespace DotPad.Runtime.Tests;

public class DumpRendererTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private enum TestColor { Red, Green, Blue }

    private sealed class Point { public int X { get; set; } public int Y { get; set; } }

    private sealed class Throwing
    {
        public string Safe => "ok";
        public string Broken => throw new InvalidOperationException("boom");
    }

    // Node with a complex child property — used to trigger depth truncation
    private sealed class Node { public string Name { get; set; } = ""; public Node? Child { get; set; } }

    // ── Null ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_Null_ProducesNullSpan()
    {
        // Act
        var result = DumpRenderer.Render(null, null);

        // Assert
        Assert.Contains("dp-null", result);
        Assert.Contains("null", result);
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_String_ProducesQuotedStringSpan()
    {
        // Act
        var result = DumpRenderer.Render("hello", null);

        // Assert
        Assert.Contains("dp-string", result);
        Assert.Contains("hello", result);
        Assert.Contains("&quot;", result);
    }

    [Fact]
    public void Render_LongString_TruncatesAt500Chars()
    {
        // Arrange
        var longStr = new string('x', 501);

        // Act
        var result = DumpRenderer.Render(longStr, null);

        // Assert
        Assert.Contains("…", result);
        Assert.DoesNotContain(longStr, result);
    }

    [Fact]
    public void Render_ShortString_NotTruncated()
    {
        // Arrange
        var s = new string('x', 500);

        // Act
        var result = DumpRenderer.Render(s, null);

        // Assert
        Assert.DoesNotContain("…", result);
    }

    [Fact]
    public void Render_BoolTrue_ProducesBoolTrueSpan()
    {
        // Act
        var result = DumpRenderer.Render(true, null);

        // Assert
        Assert.Contains("dp-bool", result);
        Assert.Contains(">true<", result);
    }

    [Fact]
    public void Render_BoolFalse_ProducesBoolFalseSpan()
    {
        // Act
        var result = DumpRenderer.Render(false, null);

        // Assert
        Assert.Contains("dp-bool", result);
        Assert.Contains(">false<", result);
    }

    [Fact]
    public void Render_Integer_ProducesNumberSpan()
    {
        // Act
        var result = DumpRenderer.Render(42, null);

        // Assert
        Assert.Contains("dp-number", result);
        Assert.Contains("42", result);
    }

    [Fact]
    public void Render_Char_ProducesSingleQuotedSpan()
    {
        // Act
        var result = DumpRenderer.Render('A', null);

        // Assert
        Assert.Contains("dp-string", result);
        Assert.Contains("&apos;", result);
        Assert.Contains("A", result);
    }

    [Fact]
    public void Render_Enum_ProducesEnumSpan()
    {
        // Act
        var result = DumpRenderer.Render(TestColor.Green, null);

        // Assert
        Assert.Contains("dp-enum", result);
        Assert.Contains("Green", result);
    }

    [Fact]
    public void Render_Guid_ProducesStringSpan()
    {
        // Arrange
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789012");

        // Act
        var result = DumpRenderer.Render(guid, null);

        // Assert
        Assert.Contains("dp-string", result);
        Assert.Contains("12345678", result);
    }

    [Fact]
    public void Render_DateTime_ProducesDatetimeSpanWithIso8601()
    {
        // Arrange
        var dt = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var result = DumpRenderer.Render(dt, null);

        // Assert
        Assert.Contains("dp-datetime", result);
        Assert.Contains("2024-06-15", result);
    }

    // ── HTML encoding ─────────────────────────────────────────────────────────

    [Fact]
    public void Render_StringWithHtmlSpecialChars_EncodesOutput()
    {
        // Act
        var result = DumpRenderer.Render("<b>test & \"check\"</b>", null);

        // Assert
        Assert.Contains("&lt;b&gt;", result);
        Assert.Contains("&amp;", result);
        Assert.Contains("&quot;check&quot;", result);
        Assert.DoesNotContain("<b>", result);
    }

    // ── Label ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_WithLabel_PrefixesLabelDiv()
    {
        // Act
        var result = DumpRenderer.Render(42, "my label");

        // Assert
        Assert.StartsWith("<div class=\"dp-label\">my label</div>", result);
    }

    [Fact]
    public void Render_LabelWithHtmlChars_LabelIsEncoded()
    {
        // Act
        var result = DumpRenderer.Render(1, "<script>");

        // Assert
        Assert.Contains("&lt;script&gt;", result);
        Assert.DoesNotContain("<script>", result);
    }

    // ── Collections ───────────────────────────────────────────────────────────

    [Fact]
    public void Render_EmptyList_ShowsEmptyMessage()
    {
        // Act
        var result = DumpRenderer.Render(new List<int>(), null);

        // Assert
        Assert.Contains("empty", result);
    }

    [Fact]
    public void Render_PrimitiveList_ShowsIndexAndValueColumns()
    {
        // Arrange
        var list = new List<int> { 10, 20, 30 };

        // Act
        var result = DumpRenderer.Render(list, null);

        // Assert
        Assert.Contains("dp-table", result);
        Assert.Contains("#", result);
        Assert.Contains("Value", result);
        Assert.Contains("10", result);
        Assert.Contains("20", result);
    }

    [Fact]
    public void Render_ListOfPocos_ShowsPropertyColumns()
    {
        // Arrange
        var list = new List<Point> { new() { X = 1, Y = 2 }, new() { X = 3, Y = 4 } };

        // Act
        var result = DumpRenderer.Render(list, null);

        // Assert
        Assert.Contains("dp-table", result);
        Assert.Contains(">X<", result);
        Assert.Contains(">Y<", result);
        Assert.Contains("1", result);
    }

    // ── Dictionary ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_Dictionary_ShowsKeyValueTable()
    {
        // Arrange
        var dict = new Dictionary<string, int> { ["alpha"] = 1, ["beta"] = 2 };

        // Act
        var result = DumpRenderer.Render(dict, null);

        // Assert
        Assert.Contains("dp-table", result);
        Assert.Contains(">Key<", result);
        Assert.Contains(">Value<", result);
        Assert.Contains("alpha", result);
        Assert.Contains("beta", result);
    }

    // ── POCO object ───────────────────────────────────────────────────────────

    [Fact]
    public void Render_PocoObject_ShowsPropertyGrid()
    {
        // Arrange
        var point = new Point { X = 5, Y = 10 };

        // Act
        var result = DumpRenderer.Render(point, null);

        // Assert
        Assert.Contains("dp-type", result);
        Assert.Contains("dp-propname", result);
        Assert.Contains("X", result);
        Assert.Contains("Y", result);
        Assert.Contains("5", result);
        Assert.Contains("10", result);
    }

    [Fact]
    public void Render_ObjectWithThrowingProperty_ShowsErrorSpan()
    {
        // Act
        var result = DumpRenderer.Render(new Throwing(), null);

        // Assert — reflection wraps exceptions in TargetInvocationException, so we just check the error class exists
        Assert.Contains("dp-error", result);
        Assert.Contains("ok", result); // Safe property still renders
    }

    // ── Depth limiting ────────────────────────────────────────────────────────

    [Fact]
    public void Render_ComplexObjectAtDepthZero_ShowsTruncated()
    {
        // Arrange
        var obj = new Point { X = 1, Y = 2 };

        // Act
        var result = DumpRenderer.Render(obj, null, maxDepth: 0);

        // Assert
        Assert.Contains("dp-truncated", result);
    }

    [Fact]
    public void Render_NestedObject_TruncatesAtMaxDepth()
    {
        // Arrange — Node.Child is a complex type; with maxDepth=1 it renders at depth 1 = maxDepth → truncated
        var list = new List<Node> { new() { Name = "root", Child = new Node { Name = "child" } } };

        // Act
        var result = DumpRenderer.Render(list, null, maxDepth: 1);

        // Assert
        Assert.Contains("dp-truncated", result);
    }
}
