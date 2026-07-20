using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DotPad.Avalonia.CodeTextEditor;
using DotPad.Contracts.IntelliSense;
using DotPad.UserControls;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #62 "inlay hints" — InlayHintRenderer, the VisualLineElementGenerator that renders
// IInlayHint.Text as a dim, non-editable decoration (see CSharpIntelliSenseInlayHintsTests.cs for the
// Roslyn-backed half that produces the hints). GetFirstInterestedOffset/IsEnabled are pure logic,
// directly unit-testable; ConstructElement needs a real TextView render pass (CurrentContext is only
// populated by AvaloniaEdit's own rendering pipeline via StartGeneration) — exercised instead by the
// end-to-end test at the bottom, same rationale as why SemanticHighlighter/SquiggleRenderer have no
// direct unit tests of their own in this codebase either.
public class InlayHintRendererTests
{
    private sealed record TestHint(int Offset, string Text) : IInlayHint;

    [Fact]
    public void GetFirstInterestedOffset_Should_ReturnMinusOne_WhenThereAreNoHints()
    {
        var renderer = new InlayHintRenderer();

        Assert.Equal(-1, renderer.GetFirstInterestedOffset(0));
    }

    [Fact]
    public void GetFirstInterestedOffset_Should_ReturnTheEarliestHint_AtOrAfterStartOffset()
    {
        var renderer = new InlayHintRenderer();
        renderer.Update([new TestHint(10, "int "), new TestHint(4, "string "), new TestHint(20, "bool ")]);

        Assert.Equal(4, renderer.GetFirstInterestedOffset(0));
        Assert.Equal(10, renderer.GetFirstInterestedOffset(5));
        Assert.Equal(20, renderer.GetFirstInterestedOffset(11));
        Assert.Equal(-1, renderer.GetFirstInterestedOffset(21));
    }

    [Fact]
    public void GetFirstInterestedOffset_Should_ReturnTheHint_ExactlyAtStartOffset()
    {
        var renderer = new InlayHintRenderer();
        renderer.Update([new TestHint(5, "int ")]);

        Assert.Equal(5, renderer.GetFirstInterestedOffset(5));
    }

    [Fact]
    public void GetFirstInterestedOffset_Should_ReturnMinusOne_WhenDisabled_EvenWithHintsPresent()
    {
        var renderer = new InlayHintRenderer { IsEnabled = false };
        renderer.Update([new TestHint(0, "int ")]);

        Assert.Equal(-1, renderer.GetFirstInterestedOffset(0));
    }

    [AvaloniaFact]
    public void RealEditor_Should_RenderAHint_WithoutThrowing_AndWithoutChangingTheDocumentText()
    {
        const string text = "var x = 5;";
        var editor = new CodeTextEditor { Text = text };
        var renderer = new InlayHintRenderer();
        editor.TextArea.TextView.ElementGenerators.Add(renderer);

        var window = new Window { Content = editor };
        window.Show();

        renderer.Update([new TestHint(text.IndexOf('x'), "int ")]);
        editor.TextArea.TextView.Redraw();

        // window.Show() (above) already ran one real layout pass with the renderer registered but no
        // hints yet; this second one — forced by measuring/arranging again — is what actually invokes
        // GetFirstInterestedOffset/ConstructElement against the hint just added, exercising
        // FormattedTextElement/HintTextSource construction for real.
        editor.Measure(Size.Infinity);
        editor.Arrange(new Rect(editor.DesiredSize));

        Assert.Equal(text, editor.Text);
    }
}
