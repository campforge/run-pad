using Invoklet.Contracts;
using Invoklet.Contracts.Models;

namespace Invoklet.Debug;

public sealed class DapBreakpointHitContext : IBreakpointHitContext
{
    private readonly TaskCompletionSource<DebugAction> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required IBreakpoint Breakpoint { get; init; }
    public int ThreadId { get; init; }
    public IReadOnlyList<IVariableInfo> LocalVariables { get; init; } = [];
    public IReadOnlyList<IStackFrame> CallStack { get; init; } = [];
    public IReadOnlyList<IThreadInfo> Threads { get; init; } = [];
    public DebugStopReason StopReason { get; init; } = DebugStopReason.Breakpoint;
    public DebugExceptionInfo? Exception { get; init; }
    public string? ResumeError { get; init; }

    internal Func<int, CancellationToken, Task<IReadOnlyList<IVariableInfo>>>? GetChildrenFunc { get; set; }
    internal Func<int, CancellationToken, Task>? RunToCursorFunc { get; set; }
    internal Func<string, CancellationToken, Task<IVariableInfo?>>? EvaluateFunc { get; set; }

    public Task<IReadOnlyList<IVariableInfo>> GetChildrenAsync(int variablesReference, CancellationToken ct = default)
        => GetChildrenFunc is { } f
            ? f(variablesReference, ct)
            : Task.FromResult<IReadOnlyList<IVariableInfo>>([]);

    public Task<IVariableInfo?> EvaluateAsync(string expression, CancellationToken ct = default)
        => EvaluateFunc is { } f ? f(expression, ct) : Task.FromResult<IVariableInfo?>(null);

    public async Task RunToCursorAsync(int line, CancellationToken ct = default)
    {
        if (RunToCursorFunc is { } f)
            await f(line, ct);
        Resume(DebugAction.Continue);
    }

    public void Resume(DebugAction action = DebugAction.Continue)
        => _tcs.TrySetResult(action);

    internal Task<DebugAction> WaitForResumeAsync(CancellationToken ct)
    {
        ct.Register(() => _tcs.TrySetCanceled(ct));
        return _tcs.Task;
    }
}

public sealed class DapStackFrame : IStackFrame
{
    public required string MethodName { get; init; }
    public required string FileName { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed class DapThreadInfo : IThreadInfo
{
    public int Id { get; init; }
    public required string Name { get; init; }
}

public sealed class SyntheticBreakpoint : IBreakpoint
{
    public SyntheticBreakpoint(string fileName, int line) { FileName = fileName; Line = line; }
    public string FileName { get; }
    public int Line { get; set; }
    public string? Condition => null;
    public string? HitCondition => null;
}
