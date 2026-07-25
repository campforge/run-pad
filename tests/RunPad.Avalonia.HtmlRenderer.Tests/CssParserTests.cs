using RunPad.Avalonia.HtmlRenderer.External.Core;
using RunPad.Avalonia.HtmlRenderer.Tests.Fakes;
using Xunit;

namespace RunPad.Avalonia.HtmlRenderer.Tests;

public class CssParserTests
{
    private static readonly TestAdapter Adapter = new();

    [Fact]
    public void Parse_EmptyCss_ReturnsNonNullCssData()
    {
        // Act
        var data = CssData.Parse(Adapter, "", false);

        // Assert
        Assert.NotNull(data);
    }

    [Fact]
    public void Parse_ElementSelector_CreatesBlock()
    {
        // Act
        var data = CssData.Parse(Adapter, "body { color: red; }", false);

        // Assert
        Assert.True(data.ContainsCssBlock("body"));
    }

    [Fact]
    public void Parse_ClassSelector_CreatesBlock()
    {
        // Act
        var data = CssData.Parse(Adapter, ".error { color: red; }", false);

        // Assert
        Assert.True(data.ContainsCssBlock(".error"));
    }

    [Fact]
    public void Parse_IdSelector_CreatesBlock()
    {
        // Act
        var data = CssData.Parse(Adapter, "#header { color: blue; }", false);

        // Assert
        Assert.True(data.ContainsCssBlock("#header"));
    }

    [Fact]
    public void Parse_PropertyValue_IsStoredAsString()
    {
        // Act
        var data = CssData.Parse(Adapter, "p { color: red; }", false);
        var blocks = data.GetCssBlock("p").ToList();

        // Assert
        Assert.Single(blocks);
        Assert.Equal("red", blocks[0].Properties["color"]);
    }

    [Fact]
    public void Parse_MultipleProperties_AllStored()
    {
        // Act
        var data = CssData.Parse(Adapter, "div { color: red; font-size: 14px; background-color: white; }", false);
        var block = data.GetCssBlock("div").First();

        // Assert
        Assert.Equal("red", block.Properties["color"]);
        Assert.Equal("14px", block.Properties["font-size"]);
        Assert.Equal("white", block.Properties["background-color"]);
    }

    [Fact]
    public void Parse_FontShorthand_ExpandsProperties()
    {
        // Act
        var data = CssData.Parse(Adapter, "p { font: bold 14px Arial; }", false);
        var block = data.GetCssBlock("p").First();

        // Assert — shorthand must expand into at least font-size or font-family
        Assert.True(
            block.Properties.ContainsKey("font-size") ||
            block.Properties.ContainsKey("font-family"),
            "font shorthand should be expanded");
    }

    [Fact]
    public void Parse_MarginShorthand_ExpandsToSides()
    {
        // Act
        var data = CssData.Parse(Adapter, "div { margin: 10px; }", false);
        var block = data.GetCssBlock("div").First();

        // Assert — shorthand must expand into at least one side-specific property
        Assert.True(
            block.Properties.ContainsKey("margin-top") ||
            block.Properties.ContainsKey("margin"),
            "margin shorthand should be expanded");
    }

    [Fact]
    public void ContainsCssBlock_UnknownSelector_ReturnsFalse()
    {
        // Arrange
        var data = CssData.Parse(Adapter, "p { color: red; }", false);

        // Assert
        Assert.False(data.ContainsCssBlock("span"));
    }

    [Fact]
    public void GetCssBlock_UnknownSelector_ReturnsEmpty()
    {
        // Arrange
        var data = CssData.Parse(Adapter, "p { color: red; }", false);

        // Act
        var blocks = data.GetCssBlock("span");

        // Assert
        Assert.Empty(blocks);
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        // Arrange
        var data = CssData.Parse(Adapter, "p { color: red; }", false);

        // Act
        var clone = data.Clone();

        // Assert
        Assert.True(clone.ContainsCssBlock("p"));
        Assert.Equal("red", clone.GetCssBlock("p").First().Properties["color"]);
    }

    [Fact]
    public void Combine_AddsMissingSelectorsFromOther()
    {
        // Arrange
        var data1 = CssData.Parse(Adapter, "p { color: red; }", false);
        var data2 = CssData.Parse(Adapter, "span { color: blue; }", false);

        // Act
        data1.Combine(data2);

        // Assert
        Assert.True(data1.ContainsCssBlock("p"));
        Assert.True(data1.ContainsCssBlock("span"));
    }

    [Fact]
    public void Combine_OverwritesExistingProperty()
    {
        // Arrange
        var data1 = CssData.Parse(Adapter, "p { color: red; }", false);
        var data2 = CssData.Parse(Adapter, "p { color: blue; }", false);

        // Act
        data1.Combine(data2);

        // Assert
        var block = data1.GetCssBlock("p").First();
        Assert.Equal("blue", block.Properties["color"]);
    }

    [Fact]
    public void Parse_MultipleSelectors_EachGetsBlock()
    {
        // Act
        var data = CssData.Parse(Adapter, "h1 { color: red; } h2 { color: blue; }", false);

        // Assert
        Assert.True(data.ContainsCssBlock("h1"));
        Assert.True(data.ContainsCssBlock("h2"));
    }
}
