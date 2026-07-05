namespace DotPad.Contracts.Models;

public enum DebugAction { Continue, StepOver, StepIn, StepOut }

public interface IThreadInfo
{
    int Id { get; }
    string Name { get; }
}

public interface IVariableInfo
{
    string Name { get; }
    string Value { get; }
    string TypeName { get; }
    bool HasChildren { get; }
    int VariablesReference { get; }
    IReadOnlyList<IVariableInfo> Children { get; }
}

public interface IStackFrame
{
    string MethodName { get; }
    string FileName { get; }
    int Line { get; }
    int Column { get; }
}

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
}

public interface IMemoryDump
{
    DateTimeOffset CapturedAt { get; }
    IReadOnlyList<IVariableInfo> Variables { get; }
}

public delegate void BreakpointHitHandler(IBreakpointHitContext context);

// Reports the debugger's verified/message response for a breakpoint after setBreakpoints —
// e.g. verified=false, message="No code at line 5".
public delegate void BreakpointVerifiedHandler(IBreakpoint breakpoint, bool verified, string? message);
