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

    [Theory]
    [InlineData(50, 50, true)]
    [InlineData(150, 50, false)]
    public void Contains_Point_ReflectsWhetherThePointIsInside(double x, double y, bool expected)
    {
        var r = new RRect(0, 0, 100, 100);

        Assert.Equal(expected, r.Contains(x, y));
    }

    [Theory]
    [InlineData(10, 10, 50, 50, true)]
    [InlineData(50, 50, 100, 100, false)]
    public void Contains_Rect_ReflectsWhetherTheOtherRectIsFullyInside(
        double otherX, double otherY, double otherWidth, double otherHeight, bool expected)
    {
        var r = new RRect(0, 0, 100, 100);
        var other = new RRect(otherX, otherY, otherWidth, otherHeight);

        Assert.Equal(expected, r.Contains(other));
    }

    [Theory]
    [InlineData(0, 0, 100, 100, 50, 50, 100, 100, true)]
    [InlineData(0, 0, 50, 50, 100, 100, 50, 50, false)]
    public void IntersectsWith_ReflectsWhetherTheTwoRectsOverlap(
        double aX, double aY, double aWidth, double aHeight,
        double bX, double bY, double bWidth, double bHeight, bool expected)
    {
        var a = new RRect(aX, aY, aWidth, aHeight);
        var b = new RRect(bX, bY, bWidth, bHeight);

        Assert.Equal(expected, a.IntersectsWith(b));
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
