namespace DotPad.Contracts;

public interface IVariableInfo
{
    string Name { get; }
    string Value { get; }
    string TypeName { get; }
    bool HasChildren { get; }
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
}

public interface IMemoryDump
{
    DateTimeOffset CapturedAt { get; }
    IReadOnlyList<IVariableInfo> Variables { get; }
}

public delegate void BreakpointHitHandler(IBreakpointHitContext context);
