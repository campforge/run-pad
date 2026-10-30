namespace Invoklet.Contracts.Debugging;

public interface IStackFrame
{
    string MethodName { get; }
    string FileName { get; }
    int Line { get; }
    int Column { get; }
}
