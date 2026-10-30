using Invoklet.Avalonia.CodeTextEditor.Completions;
using Xunit;

namespace Invoklet.NonUI.Tests.Avalonia.CodeTextEditor.Completions;

// Building the rich Description control (real Avalonia TextBlock/Run
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

    // The completion list row can show something richer than the bare name
    // (e.g. a dimmed "(in Namespace)" annotation for a not-yet-imported type — EditorTabItemControl
    // builds that via DisplaySegmentRenderer.BuildListRow), but what actually gets matched/inserted
    // must stay exactly the plain name regardless — Text and Content are independent.
    [Fact]
    public void Content_Should_UseTheProvidedOverride_WhenGiven()
    {
        var data = new EditorCompletionData("JsonConvert", () => "described", priority: 1.0, content: "custom-row");

        Assert.Equal("custom-row", data.Content);
        Assert.Equal("JsonConvert", data.Text);
    }

    [Fact]
    public void Content_Should_FallBackToDisplayText_WhenNoOverrideIsGiven()
    {
        var data = new EditorCompletionData("JsonConvert", () => "described", priority: 1.0);

        Assert.Equal("JsonConvert", data.Content);
    }
}
