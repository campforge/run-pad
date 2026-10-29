namespace Invoklet.Contracts.Models;

/// <summary>Why execution stopped: the reason the debugger reported with the stop.</summary>
public enum DebugStopReason { Breakpoint, Step, Pause, Exception, Entry }
