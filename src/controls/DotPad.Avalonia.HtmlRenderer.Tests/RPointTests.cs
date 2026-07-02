using DotPad.Avalonia.HtmlRenderer.External.Adapters.Entities;
using Xunit;

namespace DotPad.Avalonia.HtmlRenderer.Tests;

public class RPointTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        // Arrange / Act
        var p = new RPoint(10, 20);

        // Assert
        Assert.Equal(10, p.X);
        Assert.Equal(20, p.Y);
    }

    [Fact]
    public void IsEmpty_TrueForOrigin()
    {
        // Arrange
        var p = new RPoint(0, 0);

        // Assert
        Assert.True(p.IsEmpty);
    }

    [Fact]
    public void IsEmpty_FalseWhenXIsNonZero()
    {
        // Arrange
        var p = new RPoint(1, 0);

        // Assert
        Assert.False(p.IsEmpty);
    }

    [Fact]
    public void IsEmpty_FalseWhenYIsNonZero()
    {
        // Arrange
        var p = new RPoint(0, 1);

        // Assert
        Assert.False(p.IsEmpty);
    }

    [Fact]
    public void AddSize_OffsetsBySize()
    {
        // Arrange
        var p = new RPoint(10, 20);
        var s = new RSize(5, 7);

        // Act
        var result = p + s;

        // Assert
        Assert.Equal(15, result.X);
        Assert.Equal(27, result.Y);
    }

    [Fact]
    public void SubtractSize_OffsetsByNegativeSize()
    {
        // Arrange
        var p = new RPoint(10, 20);
        var s = new RSize(3, 8);

        // Act
        var result = p - s;

        // Assert
        Assert.Equal(7, result.X);
        Assert.Equal(12, result.Y);
    }

    [Fact]
    public void EqualityOperator_TrueForSameValues()
    {
        // Arrange
        var a = new RPoint(10, 20);
        var b = new RPoint(10, 20);

        // Assert
        Assert.True(a == b);
    }

    [Fact]
    public void InequalityOperator_TrueForDifferentValues()
    {
        // Arrange
        var a = new RPoint(10, 20);
        var b = new RPoint(30, 40);

        // Assert
        Assert.True(a != b);
    }
}
