using DotPad.Avalonia.HtmlRenderer.External.Adapters.Entities;
using Xunit;

namespace DotPad.Avalonia.HtmlRenderer.Tests;

public class RRectTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        // Arrange / Act
        var r = new RRect(10, 20, 100, 50);

        // Assert
        Assert.Equal(10, r.X);
        Assert.Equal(20, r.Y);
        Assert.Equal(100, r.Width);
        Assert.Equal(50, r.Height);
    }

    [Fact]
    public void Right_IsXPlusWidth()
    {
        // Arrange
        var r = new RRect(10, 20, 100, 50);

        // Assert
        Assert.Equal(110, r.Right);
    }

    [Fact]
    public void Bottom_IsYPlusHeight()
    {
        // Arrange
        var r = new RRect(10, 20, 100, 50);

        // Assert
        Assert.Equal(70, r.Bottom);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        // Assert
        Assert.True(RRect.Empty.IsEmpty);
    }

    [Fact]
    public void Contains_Point_TrueWhenInside()
    {
        // Arrange
        var r = new RRect(0, 0, 100, 100);

        // Assert
        Assert.True(r.Contains(50, 50));
    }

    [Fact]
    public void Contains_Point_FalseWhenOutside()
    {
        // Arrange
        var r = new RRect(0, 0, 100, 100);

        // Assert
        Assert.False(r.Contains(150, 50));
    }

    [Fact]
    public void Contains_Rect_TrueWhenFullyInside()
    {
        // Arrange
        var outer = new RRect(0, 0, 100, 100);
        var inner = new RRect(10, 10, 50, 50);

        // Assert
        Assert.True(outer.Contains(inner));
    }

    [Fact]
    public void Contains_Rect_FalseWhenPartiallyOutside()
    {
        // Arrange
        var r = new RRect(0, 0, 100, 100);
        var other = new RRect(50, 50, 100, 100);

        // Assert
        Assert.False(r.Contains(other));
    }

    [Fact]
    public void IntersectsWith_TrueForOverlapping()
    {
        // Arrange
        var a = new RRect(0, 0, 100, 100);
        var b = new RRect(50, 50, 100, 100);

        // Assert
        Assert.True(a.IntersectsWith(b));
    }

    [Fact]
    public void IntersectsWith_FalseForNonOverlapping()
    {
        // Arrange
        var a = new RRect(0, 0, 50, 50);
        var b = new RRect(100, 100, 50, 50);

        // Assert
        Assert.False(a.IntersectsWith(b));
    }

    [Fact]
    public void StaticIntersect_ReturnsOverlapArea()
    {
        // Arrange
        var a = new RRect(0, 0, 100, 100);
        var b = new RRect(50, 50, 100, 100);

        // Act
        var result = RRect.Intersect(a, b);

        // Assert
        Assert.Equal(new RRect(50, 50, 50, 50), result);
    }

    [Fact]
    public void StaticIntersect_NonOverlapping_ReturnsEmpty()
    {
        // Arrange
        var a = new RRect(0, 0, 10, 10);
        var b = new RRect(100, 100, 10, 10);

        // Act
        var result = RRect.Intersect(a, b);

        // Assert
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Union_ReturnsBoundingRect()
    {
        // Arrange
        var a = new RRect(0, 0, 50, 50);
        var b = new RRect(50, 50, 50, 50);

        // Act
        var result = RRect.Union(a, b);

        // Assert
        Assert.Equal(new RRect(0, 0, 100, 100), result);
    }

    [Fact]
    public void Offset_MovesRect()
    {
        // Arrange
        var r = new RRect(10, 20, 100, 50);

        // Act
        r.Offset(5, 10);

        // Assert
        Assert.Equal(15, r.X);
        Assert.Equal(30, r.Y);
    }

    [Fact]
    public void FromLTRB_CreatesCorrectRect()
    {
        // Act
        var r = RRect.FromLTRB(10, 20, 110, 70);

        // Assert
        Assert.Equal(10, r.X);
        Assert.Equal(20, r.Y);
        Assert.Equal(100, r.Width);
        Assert.Equal(50, r.Height);
    }

    [Fact]
    public void Inflate_ExpandsRect()
    {
        // Arrange
        var r = new RRect(10, 20, 100, 50);

        // Act
        r.Inflate(5, 10);

        // Assert
        Assert.Equal(5, r.X);
        Assert.Equal(10, r.Y);
        Assert.Equal(110, r.Width);
        Assert.Equal(70, r.Height);
    }
}
