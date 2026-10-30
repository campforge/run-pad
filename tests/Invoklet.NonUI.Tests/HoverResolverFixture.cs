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

    public static IDiagnosticInfo CreateDiagnostic(int start, int end, string message)
    {
        var diagnostic = new Mock<IDiagnosticInfo>();
        diagnostic.SetupGet(d => d.StartOffset).Returns(start);
        diagnostic.SetupGet(d => d.EndOffset).Returns(end);
        diagnostic.SetupGet(d => d.Message).Returns(message);
        return diagnostic.Object;
    }

    public IQuickInfoResult SetupQuickInfo()
    {
        var info = new Mock<IQuickInfoResult>().Object;
        IntelliSense.Setup(i => i.GetQuickInfoAsync(Text, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(info);
        return info;
    }

    public HoverRequest CreateRequest(int offset, IBreakpointHitContext? context = null,
        IReadOnlyList<IVariableInfo>? locals = null, params IDiagnosticInfo[] diagnostics)
        => new(Text, offset, null, context, locals, diagnostics, () => ContextIsCurrent);

    public HoverResolver CreateSut() => new(Debug.Object, IntelliSense.Object);
}
