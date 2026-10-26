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
    public void Extract_Should_UseLanguageSpecificIdentifierCharacters()
        => Assert.Equal("foo-bar", DebugHoverSymbolExtractor.Extract("foo-bar", 5, new HashSet<char> { '-' }));

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

    [Fact]
    public void ResolveSnapshot_ShouldRejectSyntheticNames()
    {
        Assert.Null(DebugHoverController.ResolveSnapshot([
            new Variable("Locals", "scope", ""), new Variable("Arguments", "scope", "")], "Locals"));
    }

    [Fact]
    public void ResolveSnapshot_ShouldReturnTheSingleMatchingVariable()
    {
        var value = new Variable("count", "int", "42");
        Assert.Same(value, DebugHoverController.ResolveSnapshot([value], "count"));
    }

    [Fact]
    public void Begin_ShouldCancelPreviousGeneration()
    {
        var controller = new DebugHoverController();
        var first = controller.Begin();
        controller.Cancel();
        var second = controller.Begin();
        Assert.False(controller.IsCurrent(first.Generation));
        Assert.True(controller.IsCurrent(second.Generation));
    }

    [Fact]
    public async Task LoadChildren_ShouldHonorDepthBreadthAndZeroReference()
    {
        var context = new ChildContext(Enumerable.Range(0, 100)
            .Select(i => (IVariableInfo)new Variable($"v{i}", "int", i.ToString())).ToArray());
        var controller = new DebugHoverController();
        var parent = new Variable("root", "T", "{}", hasChildren: true, variablesReference: 1);

        Assert.Empty(await controller.LoadChildrenAsync(context, parent, DebugHoverController.MaxDepth, TestContext.Current.CancellationToken));
        Assert.Empty(await controller.LoadChildrenAsync(context, new Variable("empty", "T", "", true, 0), 0, TestContext.Current.CancellationToken));
        Assert.Equal(DebugHoverController.MaxChildren,
            (await controller.LoadChildrenAsync(context, parent, 0, TestContext.Current.CancellationToken)).Count);
        Assert.Equal(1, context.Calls);
    }

    [Fact]
    public void TreeBudget_ShouldCapOneHoverTree()
    {
        var budget = new DebugHoverController.TreeBudget();
        for (var i = 0; i < DebugHoverController.MaxTotalRows; i++) Assert.True(budget.TryReserve());
        Assert.False(budget.TryReserve());
    }

    [Theory]
    [InlineData("obj.Child", true)]
    [InlineData("obj.Child.Name", true)]
    [InlineData("obj.Child()", false)]
    [InlineData("obj[0]", false)]
    [InlineData("obj = value", false)]
    public void HoverExpressionPolicy_ShouldAllowOnlyDottedIdentifiers(string expression, bool expected)
        => Assert.Equal(expected, DebugHoverController.IsSafeHoverExpression(expression));

    [Fact]
    public async Task EvaluateHover_ShouldNotCallDapForUnsafeExpression()
    {
        var context = new ChildContext([]);
        Assert.Null(await new DebugHoverController().EvaluateHoverAsync(context, "obj[0]", TestContext.Current.CancellationToken));
        Assert.Equal(0, context.EvaluateCalls);
    }

    [Fact]
    public async Task EvaluateHover_ShouldCallDapForSafeUnicodeExpression()
    {
        var context = new ChildContext([]);
        Assert.Null(await new DebugHoverController().EvaluateHoverAsync(context, "привет.имя", TestContext.Current.CancellationToken));
        Assert.Equal(1, context.EvaluateCalls);
    }

    [Fact]
    public async Task LoadChildren_ShouldTimeout()
    {
        var controller = new DebugHoverController();
        var parent = new Variable("root", "T", "{}", true, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.LoadChildrenAsync(new BlockingContext(), parent, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadChildren_ShouldHonorCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var parent = new Variable("root", "T", "{}", true, 1);
        var load = new DebugHoverController().LoadChildrenAsync(
            new BlockingContext(), parent, 0, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    [Fact]
    public void Cancel_ShouldInvalidateAnInFlightTreeGeneration()
    {
        var controller = new DebugHoverController();
        var hover = controller.Begin();

        controller.Cancel();

        Assert.False(controller.IsCurrent(hover.Generation));
    }

    [Fact]
    public void BeginCancelBegin_ShouldKeepOnlyTheNewestGenerationCurrent()
    {
        using var controller = new DebugHoverController();
        var first = controller.Begin();
        controller.Cancel();
        var second = controller.Begin();

        Assert.False(controller.IsCurrent(first.Generation));
        Assert.True(controller.IsCurrent(second.Generation));
    }

    [Fact]
    public async Task Dispose_ShouldCancelAnInFlightChildrenRequest()
    {
        using var controller = new DebugHoverController();
        var hover = controller.Begin();
        var parent = new Variable("root", "T", "{}", true, 1);
        var load = controller.LoadChildrenAsync(new BlockingContext(), parent, 0, hover.Token);

        controller.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.False(controller.IsCurrent(hover.Generation));
    }

    private class ChildContext(IReadOnlyList<IVariableInfo> children) : IBreakpointHitContext
    {
        public int Calls { get; private set; }
        public int EvaluateCalls { get; private set; }
        public IBreakpoint Breakpoint => throw new NotSupportedException();
        public int ThreadId => 1;
        public IReadOnlyList<IVariableInfo> LocalVariables => [];
        public IReadOnlyList<IStackFrame> CallStack => [];
        public IReadOnlyList<IThreadInfo> Threads => [];
        public void Resume(DebugAction action = DebugAction.Continue) { }
        public virtual Task<IReadOnlyList<IVariableInfo>> GetChildrenAsync(int variablesReference, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(children);
        }
        public Task RunToCursorAsync(int line, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IVariableInfo?> EvaluateAsync(string expression, CancellationToken ct = default)
        {
            EvaluateCalls++;
            return Task.FromResult<IVariableInfo?>(null);
        }
    }

    private sealed class BlockingContext : ChildContext
    {
        public BlockingContext() : base([]) { }
        public override async Task<IReadOnlyList<IVariableInfo>> GetChildrenAsync(int variablesReference, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }
    }

    private sealed record Variable(string Name, string TypeName, string Value,
        bool hasChildren = false, int variablesReference = 0) : IVariableInfo
    {
        public bool HasChildren => hasChildren;
        public int VariablesReference => variablesReference;
        public IReadOnlyList<IVariableInfo> Children => [];
    }
}
