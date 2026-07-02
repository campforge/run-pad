using DotPad.Avalonia.HtmlRenderer.External.Adapters.Entities;
using Xunit;

namespace DotPad.Avalonia.HtmlRenderer.Tests;

public class RColorTests
{
    [Fact]
    public void FromArgb_RGB_SetsAlphaTo255()
    {
        // Arrange / Act
        var color = RColor.FromArgb(100, 150, 200);

        // Assert
        Assert.Equal(255, color.A);
    }

    [Fact]
    public void FromArgb_ARGB_SetsAllChannels()
    {
        // Arrange / Act
        var color = RColor.FromArgb(128, 100, 150, 200);

        // Assert
        Assert.Equal(128, color.A);
        Assert.Equal(100, color.R);
        Assert.Equal(150, color.G);
        Assert.Equal(200, color.B);
    }

    [Fact]
    public void Black_HasZeroRGBAndFullAlpha()
    {
        // Arrange / Act
        var c = RColor.Black;

        // Assert
        Assert.Equal(0, c.R);
        Assert.Equal(0, c.G);
        Assert.Equal(0, c.B);
        Assert.Equal(255, c.A);
    }

    [Fact]
    public void White_HasMaxRGBAndFullAlpha()
    {
        // Arrange / Act
        var c = RColor.White;

        // Assert
        Assert.Equal(255, c.R);
        Assert.Equal(255, c.G);
        Assert.Equal(255, c.B);
        Assert.Equal(255, c.A);
    }

    [Fact]
    public void Empty_IsEmpty()
    {
        // Assert
        Assert.True(RColor.Empty.IsEmpty);
    }

    [Fact]
    public void NonEmptyColor_IsNotEmpty()
    {
        // Assert
        Assert.False(RColor.Black.IsEmpty);
    }

    [Fact]
    public void EqualityOperator_ReturnsTrueForSameValues()
    {
        // Arrange
        var a = RColor.FromArgb(255, 100, 150, 200);
        var b = RColor.FromArgb(255, 100, 150, 200);

        // Assert
        Assert.True(a == b);
    }

    [Fact]
    public void InequalityOperator_ReturnsTrueForDifferentValues()
    {
        // Arrange
        var a = RColor.FromArgb(255, 100, 0, 0);
        var b = RColor.FromArgb(255, 0, 100, 0);

        // Assert
        Assert.True(a != b);
    }

    [Fact]
    public void Transparent_HasZeroAlpha()
    {
        // Assert
        Assert.Equal(0, RColor.Transparent.A);
    }
}
