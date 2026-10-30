namespace Invoklet.Avalonia.CodeTextEditor;

/// <summary>Debugger-reported state of a breakpoint, driven by the DAP setBreakpoints response.</summary>
public enum BreakpointVerificationState
{
    /// <summary>No debug session running, or the response hasn't arrived yet.</summary>
    Pending,
    /// <summary>The debugger bound the breakpoint to real code.</summary>
    Verified,
    /// <summary>The debugger rejected it (e.g. "No code at line N") — see <see cref="IBreakpoint.VerificationMessage"/>.</summary>
    Unverified,
}
