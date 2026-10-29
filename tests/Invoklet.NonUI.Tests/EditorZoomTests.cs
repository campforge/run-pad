using Invoklet.UserControls;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Code zoom steps: Zoom In/Out (wheel, View menu, status-bar menu) move to the nearest browser-style
// step, also from an in-between value a settings file may hold.
public class EditorZoomTests
{
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
