using DotPad.Contracts;
using DotPad.Contracts.Models;

namespace DotPad.Languages.CSharp.Debug;

internal sealed class DapBreakpointHitContext : IBreakpointHitContext
{
    private readonly TaskCompletionSource<DebugAction> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required IBreakpoint Breakpoint { get; init; }
    public int ThreadId { get; init; }
    public IReadOnlyList<IVariableInfo> LocalVariables { get; init; } = [];
    public IReadOnlyList<IStackFrame> CallStack { get; init; } = [];
    public IReadOnlyList<IThreadInfo> Threads { get; init; } = [];

    internal Func<int, CancellationToken, Task<IReadOnlyList<IVariableInfo>>>? GetChildrenFunc { get; set; }

    public Task<IReadOnlyList<IVariableInfo>> GetChildrenAsync(int variablesReference, CancellationToken ct = default)
        => GetChildrenFunc is { } f
            ? f(variablesReference, ct)
            : Task.FromResult<IReadOnlyList<IVariableInfo>>([]);

    public void Resume(DebugAction action = DebugAction.Continue)
        => _tcs.TrySetResult(action);

    internal Task<DebugAction> WaitForResumeAsync(CancellationToken ct)
    {
        ct.Register(() => _tcs.TrySetCanceled(ct));
        return _tcs.Task;
    }
}

internal sealed class DapStackFrame : IStackFrame
{
    public required string MethodName { get; init; }
    public required string FileName { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

internal sealed class DapThreadInfo : IThreadInfo
{
    public int Id { get; init; }
    public required string Name { get; init; }
}

internal sealed class SyntheticBreakpoint : IBreakpoint
{
    public SyntheticBreakpoint(string fileName, int line) { FileName = fileName; Line = line; }
    public string FileName { get; }
    public int Line { get; set; }
}
