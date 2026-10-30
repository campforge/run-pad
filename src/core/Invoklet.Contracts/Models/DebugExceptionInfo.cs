namespace Invoklet.Contracts.Models;

/// <summary>
/// The exception a debug session stopped on. Any part may be missing: debuggers differ in what they
/// report, and the detailed query can be unsupported or fail, leaving only the stop's own text.
/// </summary>
/// <param name="TypeName">Full type name, e.g. <c>System.FormatException</c>.</param>
/// <param name="Message">The exception's <c>Message</c>.</param>
/// <param name="Description">The debugger's one-line description of the stop.</param>
/// <param name="StackTrace">The exception's own stack trace, as the runtime formats it.</param>
/// <param name="IsUnhandled">
/// True when the debugger reported that nothing catches the exception, so continuing ends the
/// script with it. False when it is caught or the debugger didn't say.
/// </param>
/// <param name="InnerException">The first inner exception, if any.</param>
public sealed record DebugExceptionInfo(
    string? TypeName,
    string? Message,
    string? Description,
    string? StackTrace,
    bool IsUnhandled,
    DebugExceptionInfo? InnerException = null);
