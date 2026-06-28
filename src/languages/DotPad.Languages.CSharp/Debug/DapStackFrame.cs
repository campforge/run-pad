using DotPad.Contracts;

namespace DotPad.Languages.CSharp.Debug;

internal sealed class DapStackFrame : IStackFrame
{
    public required string MethodName { get; init; }
    public required string FileName { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}
