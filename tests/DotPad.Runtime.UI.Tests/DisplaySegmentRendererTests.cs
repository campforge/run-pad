using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DotPad.UserControls;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// ANALYSIS.md TODO #59 remainder: BuildListRow now shows a dimmed "(+N overloads)" annotation next to
// a method name that collapses several overloads (ICompletionItem.OverloadCount), alongside the
// pre-existing "(in Namespace)" annotation for not-yet-imported types (item 60 point 6) — the two
// never co-occur, but both are exercised here since neither had direct test coverage before.
public class DisplaySegmentRendererTests
{
    [AvaloniaFact]
    public void BuildListRow_Should_ReturnABareTextBlock_WhenNoAnnotationApplies()
    {
        var control = DisplaySegmentRenderer.BuildListRow("Foo", detail: null, overloadCount: 1);

        var textBlock = Assert.IsType<TextBlock>(control);
        Assert.Equal("Foo", textBlock.Text);
    }

    [AvaloniaFact]
    public void BuildListRow_Should_ShowInNamespaceAnnotation_WhenDetailIsSet()
    {
        var control = DisplaySegmentRenderer.BuildListRow("JsonConvert", detail: "Newtonsoft.Json", overloadCount: 1);

        var panel = Assert.IsType<StackPanel>(control);
        var texts = panel.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("JsonConvert", texts);
        Assert.Contains("(in Newtonsoft.Json)", texts);
    }

    [AvaloniaFact]
    public void BuildListRow_Should_ShowOverloadCountAnnotation_WhenMoreThanOneOverload()
    {
        var control = DisplaySegmentRenderer.BuildListRow("WriteLine", detail: null, overloadCount: 3);

        var panel = Assert.IsType<StackPanel>(control);
        var texts = panel.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("WriteLine", texts);
        Assert.Contains("(+2 overloads)", texts);
    }
}
