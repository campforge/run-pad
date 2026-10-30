namespace Invoklet.Services;

/// <summary>How a run ended, which decides what the status bar says and whether a failure is reported.</summary>
public enum RunEnd
{
    Cancelled,

    /// <summary>The debuggee stopped on an exception nothing handled.</summary>
    UnhandledException,

    /// <summary>The script ran and did not succeed (a non-zero exit, for one).</summary>
    Failed,

    Succeeded,

    /// <summary>The run already reported an error of its own (a compile error, say): the status stays as that left it.</summary>
    ReportedError,
}

public static class RunEndDecision
{
    /// <summary>The first that applies, in this order: cancelled, unhandled exception, failed, an error already reported, success.</summary>
    public static RunEnd Of(bool cancelled, bool unhandledException, bool failed, bool errorAlreadyReported)
    {
        if (cancelled) return RunEnd.Cancelled;
        if (unhandledException) return RunEnd.UnhandledException;
        if (failed) return RunEnd.Failed;
        return errorAlreadyReported ? RunEnd.ReportedError : RunEnd.Succeeded;
    }
}
