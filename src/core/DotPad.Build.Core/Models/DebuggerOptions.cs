namespace DotPad.Build.Core.Models;

public class DebuggerOptions
{
    public string? Disable { get; set; }
    public required string DebuggerPath { get; set; }
    public DebuggerType DebuggerType { get; set; }
}
