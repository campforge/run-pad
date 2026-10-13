using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using RunPad.UserControls;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// BuildListRow now shows a dimmed "(+N overloads)" annotation next to
// a method name that collapses several overloads (ICompletionItem.OverloadCount), alongside the
// pre-existing "(in Namespace)" annotation for not-yet-imported types (item 60 point 6) — the two
// never co-occur, but both are exercised here since neither had direct test coverage before. Item 60
// point 7 (ICompletionItem.IsOwnMember) adds an orthogonal bold/normal weight on the name itself.
public class DisplaySegmentRendererTests
{
    [AvaloniaFact]
    public void BuildListRow_Should_ReturnABareTextBlock_WhenNoAnnotationApplies()
    {
        var control = DisplaySegmentRenderer.BuildListRow("Foo", detail: null, overloadCount: 1);

        var textBlock = Assert.IsType<TextBlock>(control);
        Assert.Equal("Foo", textBlock.Text);
        Assert.Equal(FontWeight.Normal, textBlock.FontWeight);
    }

    [AvaloniaFact]
    public void BuildListRow_Should_RenderTheNameBold_WhenIsOwnMemberIsTrue()
    {
        var control = DisplaySegmentRenderer.BuildListRow("Bar", detail: null, overloadCount: 1, isOwnMember: true);

        var textBlock = Assert.IsType<TextBlock>(control);
        Assert.Equal(FontWeight.Bold, textBlock.FontWeight);
    }

    // isOwnMember is orthogonal to the annotation logic — a bold name can still carry the
    // "(+N overloads)"/"(in Namespace)" dimmed annotation alongside it.
    [AvaloniaFact]
    public void BuildListRow_Should_RenderTheNameBold_AlongsideAnOverloadCountAnnotation()
    {
        var control = DisplaySegmentRenderer.BuildListRow("Foo", detail: null, overloadCount: 2, isOwnMember: true);

        var panel = Assert.IsType<StackPanel>(control);
        var nameBlock = Assert.IsType<TextBlock>(panel.Children[0]);
        Assert.Equal("Foo", nameBlock.Text);
        Assert.Equal(FontWeight.Bold, nameBlock.FontWeight);
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
