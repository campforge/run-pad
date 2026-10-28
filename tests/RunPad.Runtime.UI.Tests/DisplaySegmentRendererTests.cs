using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts.IntelliSense;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
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

    // Parameter info for a long signature: the segments Roslyn produces for a method with
    // generic, tuple and defaulted parameters.
    private const string LongSignatureCode =
        "Net.Send(|\n" +
        "static class Net\n{\n" +
        "    public static (int Code, string Text) Send(Dictionary<string, List<int>> headers, (int Low, int High) range,\n" +
        "        string separator = \", \", int[] ports = null, bool secure = false, TimeSpan timeout = default) => default;\n" +
        "}\n";

    private static async Task<IReadOnlyList<DisplayTextSegment>> LongSignatureAsync()
    {
        var referenceService = new CSharpReferenceService();
        using var service = new CSharpIntelliSenseService(
            referenceService.GetDefault().Append(referenceService.GetExtensionReference()),
            NullLogger<CSharpIntelliSenseService>.Instance);
        var position = LongSignatureCode.IndexOf('|');
        var code = LongSignatureCode.Remove(position, 1);
        var result = await service.GetSignatureHelpAsync(code, position, ct: TestContext.Current.CancellationToken);
        return result!.Items.Single().Signature;
    }

    private static string Text(IEnumerable<DisplayTextSegment> segments) => string.Concat(segments.Select(s => s.Text));

    [AvaloniaFact]
    public async Task SplitParameters_Should_SplitAtTopLevelCommasOnly()
    {
        var split = DisplaySegmentRenderer.SplitParameters(await LongSignatureAsync());

        Assert.NotNull(split);
        Assert.EndsWith("Send(", Text(split!.Value.Head));
        Assert.StartsWith("(int Code, string Text)", Text(split.Value.Head));
        Assert.Equal(")", Text(split.Value.Tail));
        Assert.Equal(
            [
                "Dictionary<string, List<int>> headers",
                "(int Low, int High) range",
                "string separator = \", \"",
                "int[] ports = null",
                "bool secure = false",
                "TimeSpan timeout = default(TimeSpan)",
            ],
            split.Value.Parameters.Select(Text));
    }

    [AvaloniaFact]
    public void SplitParameters_Should_ReturnNull_WithoutParameters()
    {
        IReadOnlyList<DisplayTextSegment> signature =
        [
            new("void", SemanticTokenKind.Keyword), new(" ", SemanticTokenKind.None),
            new("Run", SemanticTokenKind.MethodName), new("(", SemanticTokenKind.None), new(")", SemanticTokenKind.None),
        ];

        Assert.Null(DisplaySegmentRenderer.SplitParameters(signature));
    }

    [AvaloniaFact]
    public async Task ParameterInfoSignature_Should_PutEachParameterOnItsOwnLine_WhenTooWide()
    {
        var block = DisplaySegmentRenderer.BuildParameterInfoSignature(await LongSignatureAsync(), isDark: false, maxWidth: 400, charWidth: 8);

        Assert.Equal(400, block.MaxWidth);
        Assert.Equal(7, block.Inlines!.OfType<LineBreak>().Count());
        var lines = string.Concat(block.Inlines!.Select(i => i is LineBreak ? "\n" : ((Run)i).Text)).Split('\n');
        Assert.Equal("    Dictionary<string, List<int>> headers,", lines[1]);
        Assert.Equal("    TimeSpan timeout = default(TimeSpan)", lines[6]);
        Assert.Equal(")", lines[7]);
    }

    [AvaloniaFact]
    public async Task ParameterInfoSignature_Should_StayOnOneLine_WhenItFits()
    {
        var block = DisplaySegmentRenderer.BuildParameterInfoSignature(await LongSignatureAsync(), isDark: false, maxWidth: 4000, charWidth: 8);

        Assert.Empty(block.Inlines!.OfType<LineBreak>());
        Assert.Equal(4000, block.MaxWidth);
    }

    [AvaloniaFact]
    public async Task ParameterInfoSignature_Should_KeepTheActiveParameterBold_AfterWrapping()
    {
        var signature = (await LongSignatureAsync())
            .Select(s => s with { Emphasis = s.Text == "range" })
            .ToList();

        var block = DisplaySegmentRenderer.BuildParameterInfoSignature(signature, isDark: false, maxWidth: 400, charWidth: 8);

        var bold = Assert.Single(block.Inlines!.OfType<Run>(), r => r.IsSet(TextElement.FontWeightProperty));
        Assert.Equal(FontWeight.Bold, bold.FontWeight);
        Assert.Equal("range", bold.Text);
    }
}
