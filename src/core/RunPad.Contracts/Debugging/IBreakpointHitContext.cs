namespace RunPad.Contracts.Models;

public interface IBreakpointHitContext
{
    IBreakpoint Breakpoint { get; }
    int ThreadId { get; }
    IReadOnlyList<IVariableInfo> LocalVariables { get; }
    IReadOnlyList<IStackFrame> CallStack { get; }
    IReadOnlyList<IThreadInfo> Threads { get; }
    void Resume(DebugAction action = DebugAction.Continue);
    Task<IReadOnlyList<IVariableInfo>> GetChildrenAsync(int variablesReference, CancellationToken ct = default);
    Task RunToCursorAsync(int line, CancellationToken ct = default);

    /// <summary>
    /// Evaluates a watch expression in the context of the current stack frame (DAP "evaluate").
    /// Returns null if the debugger doesn't support evaluation or the session has since resumed.
    /// </summary>
    Task<IVariableInfo?> EvaluateAsync(string expression, CancellationToken ct = default);
}
