using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class HoverResolverFixture
{
    public const string Text = "var total = count + 1;";

    public Mock<IDebugHoverSource> Debug { get; } = new();

    public Mock<IIntelliSenseService> IntelliSense { get; } = new();

    public bool ContextIsCurrent { get; set; } = true;

    /// <summary>The offset of a place inside the word <paramref name="word"/> of <see cref="Text"/>.</summary>
    public static int Inside(string word) => Text.IndexOf(word, StringComparison.Ordinal) + 1;

    public static IVariableInfo CreateVariable(string name, string value = "7")
    {
        var variable = new Mock<IVariableInfo>();
        variable.SetupGet(v => v.Name).Returns(name);
        variable.SetupGet(v => v.Value).Returns(value);
        return variable.Object;
    }

    public static HoverDiagnostic CreateDiagnostic(int start, int end, string message) => new(start, end, message);

    /// <summary>A diagnostic of the editor, whose offsets move as the text is edited.</summary>
    public static (IDiagnosticInfo Segment, Action Shift) CreateMovingDiagnostic(int start, int end, string message)
    {
        var offsets = (Start: start, End: end);
        var segment = new Mock<IDiagnosticInfo>();
        segment.SetupGet(d => d.StartOffset).Returns(() => offsets.Start);
        segment.SetupGet(d => d.EndOffset).Returns(() => offsets.End);
        segment.SetupGet(d => d.Message).Returns(message);
        return (segment.Object, () => offsets = (offsets.Start + 10, offsets.End + 10));
    }

    public IQuickInfoResult SetupQuickInfo()
    {
        var info = new Mock<IQuickInfoResult>().Object;
        IntelliSense.Setup(i => i.GetQuickInfoAsync(Text, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(info);
        return info;
    }

    public HoverRequest CreateRequest(int offset, IBreakpointHitContext? context = null,
        IReadOnlyList<IVariableInfo>? locals = null, params HoverDiagnostic[] diagnostics)
        => new(Text, offset, null, context, locals, diagnostics, () => ContextIsCurrent);

    public HoverResolver CreateSut() => new(Debug.Object, IntelliSense.Object);
}
