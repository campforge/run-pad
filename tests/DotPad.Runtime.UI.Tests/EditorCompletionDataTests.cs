using DotPad.Avalonia.CodeTextEditor;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #56: building the rich Description control (real Avalonia TextBlock/Run
// construction via DisplaySegmentRenderer) eagerly for every completion item — up to ~584 of them
// on a single 24/7 auto-invoke — was the main perceived-latency contributor, since only one item's
// Description is ever visible (whichever is currently highlighted in the list).
public class EditorCompletionDataTests
{
    [Fact]
    public void Description_Should_NotInvokeTheFactory_UntilItIsActuallyRead()
    {
        var built = false;
        var data = new EditorCompletionData("Foo", () => { built = true; return "described"; }, priority: 1.0);

        Assert.False(built);
    }

    [Fact]
    public void Description_Should_InvokeTheFactory_WhenRead()
    {
        var built = false;
        var data = new EditorCompletionData("Foo", () => { built = true; return "described"; }, priority: 1.0);

        var description = data.Description;

        Assert.True(built);
        Assert.Equal("described", description);
    }

    [Fact]
    public void Description_Should_OnlyInvokeTheFactoryOnce_WhenReadRepeatedly()
    {
        var callCount = 0;
        var data = new EditorCompletionData("Foo", () => { callCount++; return "described"; }, priority: 1.0);

        _ = data.Description;
        _ = data.Description;

        Assert.Equal(1, callCount);
    }
}
