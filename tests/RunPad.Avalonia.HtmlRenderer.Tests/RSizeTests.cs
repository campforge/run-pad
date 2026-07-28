using RunPad.Avalonia.HtmlRenderer.External.Adapters.Entities;
using Xunit;

namespace RunPad.Avalonia.HtmlRenderer.Tests;

public class RSizeTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        // Arrange / Act
        var s = new RSize(100, 50);

        // Assert
        Assert.Equal(100, s.Width);
        Assert.Equal(50, s.Height);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void IsEmpty_ReflectsWhetherEitherComponentIsNonZero(double width, double height, bool expected)
    {
        var s = new RSize(width, height);

        Assert.Equal(expected, s.IsEmpty);
    }

    [Fact]
    public void AdditionOperator_AddsComponents()
    {
        // Arrange
        var a = new RSize(10, 20);
        var b = new RSize(5, 3);

        // Act
        var result = a + b;

        // Assert
        Assert.Equal(15, result.Width);
        Assert.Equal(23, result.Height);
    }

    [Fact]
    public void SubtractionOperator_SubtractsComponents()
    {
        // Arrange
        var a = new RSize(10, 20);
        var b = new RSize(3, 5);

        // Act
        var result = a - b;

        // Assert
        Assert.Equal(7, result.Width);
        Assert.Equal(15, result.Height);
    }

    [Fact]
    public void ToPointF_ReturnsPointWithSameValues()
    {
        // Arrange
        var s = new RSize(30, 40);

        // Act
        var p = s.ToPointF();

        // Assert
        Assert.Equal(30, p.X);
        Assert.Equal(40, p.Y);
    }
}
