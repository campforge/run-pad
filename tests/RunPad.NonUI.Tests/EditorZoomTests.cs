using RunPad.UserControls;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Status-bar editor zoom: the percent shown is the editor FontSize relative to the Preferences font
// size, and Zoom In/Out move to the nearest browser-style step even from an in-between Ctrl+wheel zoom.
public class EditorZoomTests
{
    [Theory]
    [InlineData(12, 12, 100)]
    [InlineData(13, 12, 108)]
    [InlineData(6, 12, 50)]
    [InlineData(24, 12, 200)]
    [InlineData(12, 0, 100)]
    public void ToPercent_Should_RelateFontSizeToTheBaseFontSize(double fontSize, double baseFontSize, int expected)
    {
        Assert.Equal(expected, EditorZoom.ToPercent(fontSize, baseFontSize));
    }

    [Theory]
    [InlineData(100, 12, 12)]
    [InlineData(150, 12, 18)]
    [InlineData(50, 12, 6)]
    [InlineData(1, 12, 1)]
    public void ToFontSize_Should_ScaleTheBaseFontSize_AndNeverGoBelowOnePoint(int percent, double baseFontSize, double expected)
    {
        Assert.Equal(expected, EditorZoom.ToFontSize(percent, baseFontSize));
    }

    [Theory]
    [InlineData(100, 110)]
    [InlineData(108, 110)]
    [InlineData(110, 125)]
    [InlineData(30, 50)]
    [InlineData(300, 300)]
    [InlineData(400, 400)]
    public void NextStep_Should_ReturnTheFirstStepAboveTheCurrentPercent(int percent, int expected)
    {
        Assert.Equal(expected, EditorZoom.NextStep(percent));
    }

    [Theory]
    [InlineData(100, 90)]
    [InlineData(108, 100)]
    [InlineData(50, 50)]
    [InlineData(30, 30)]
    [InlineData(400, 300)]
    public void PreviousStep_Should_ReturnTheLastStepBelowTheCurrentPercent(int percent, int expected)
    {
        Assert.Equal(expected, EditorZoom.PreviousStep(percent));
    }
}
