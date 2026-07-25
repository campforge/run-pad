using RunPad.Avalonia.HtmlRenderer.External.Adapters.Entities;
using Xunit;

namespace RunPad.Avalonia.HtmlRenderer.Tests;

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

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void IsEmpty_ReflectsWhetherEitherComponentIsNonZero(double x, double y, bool expected)
    {
        var p = new RPoint(x, y);

        Assert.Equal(expected, p.IsEmpty);
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
