namespace DotPad.Build.Core.Models;

public class ExecutionOptions
{
    public required RuntimeOptions Runtime { get; set; }
    public required DebuggerOptions Debugger { get; set; }
    public required string WorkingDirectory { get; set; }
    public bool CreateWindow { get; set; } = false;
}
