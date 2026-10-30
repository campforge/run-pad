using Invoklet.Contracts.Models;
using Invoklet.Services;
using Invoklet.UserControls.Models;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class WatchEvaluatorFixture
{
    public IBreakpointHitContext? Context { get; set; }

    public bool Listed { get; set; } = true;

    public Mock<IBreakpointHitContext> CreateContext()
    {
        var context = new Mock<IBreakpointHitContext>();
        Context = context.Object;
        return context;
    }

    public static IVariableInfo CreateResult(string value, string typeName)
    {
        var result = new Mock<IVariableInfo>();
        result.SetupGet(r => r.Value).Returns(value);
        result.SetupGet(r => r.TypeName).Returns(typeName);
        return result.Object;
    }

    public static WatchExpression CreateWatch(string expression) => new() { Expression = expression };

    public WatchEvaluator CreateSut() => new(() => Context, _ => Listed);
}
