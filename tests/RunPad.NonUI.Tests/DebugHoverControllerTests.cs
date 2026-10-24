using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.UserControls;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class DebugHoverControllerTests
{
    [Theory]
    [InlineData("value", 1, "value")]
    [InlineData("value + other", 8, "other")]
    [InlineData("привет", 2, "привет")]
    public void Extract_Should_ReturnIdentifierAtOffset(string text, int offset, string expected)
        => Assert.Equal(expected, DebugHoverSymbolExtractor.Extract(text, offset));

    [Fact]
    public void Controller_ShouldInvalidateOlderGeneration()
    {
        var controller = new DebugHoverController();
        var first = controller.Begin();
        var second = controller.Begin();
        Assert.False(controller.IsCurrent(first.Generation));
        Assert.True(controller.IsCurrent(second.Generation));
        controller.Cancel();
        Assert.False(controller.IsCurrent(second.Generation));
    }

    [Fact]
    public void ResolveSnapshot_ShouldRejectDuplicateNames()
    {
        var value = new Variable("count", "int", "42");
        var duplicate = new Variable("count", "int", "7");
        Assert.Null(DebugHoverController.ResolveSnapshot([value, duplicate], "count"));
    }

    private sealed record Variable(string Name, string TypeName, string Value) : IVariableInfo
    {
        public bool HasChildren => false;
        public int VariablesReference => 0;
        public IReadOnlyList<IVariableInfo> Children => [];
    }
}
