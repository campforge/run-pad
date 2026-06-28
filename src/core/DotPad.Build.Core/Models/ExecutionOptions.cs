namespace DotPad.Build.Core.Models;

public class ExecutionOptions
{
    public RuntimeOptions Runtime { get; set; }
    public DebuggerOptions Debugger { get; set; }
    public string WorkingDirectory { get; set; }
    public bool CreateWindow { get; set; } = false;
}
