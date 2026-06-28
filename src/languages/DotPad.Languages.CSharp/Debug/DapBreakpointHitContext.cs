using DotPad.Contracts;

namespace DotPad.Languages.CSharp.Debug;

internal sealed class DapBreakpointHitContext : IBreakpointHitContext
{
    public required IBreakpoint Breakpoint { get; init; }
    public int ThreadId { get; init; }
    public IReadOnlyList<IVariableInfo> LocalVariables { get; init; } = [];
    public IReadOnlyList<IStackFrame> CallStack { get; init; } = [];
}

// Used when the stopped breakpoint cannot be matched to a registered IBreakpoint.
internal sealed class SyntheticBreakpoint : IBreakpoint
{
    public SyntheticBreakpoint(string fileName, int line)
    {
        FileName = fileName;
        Line = line;
    }

    public string FileName { get; }
    public int Line { get; set; }
}
