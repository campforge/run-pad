namespace Invoklet.Contracts.Debugging.Delegates;

public delegate void BreakpointVerifiedHandler(IBreakpoint breakpoint, bool verified, string? message);
