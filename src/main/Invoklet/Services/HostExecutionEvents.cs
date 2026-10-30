using Invoklet.Contracts.Models;

namespace Invoklet.Services;

public abstract record HostExecutionEvent
{
    private HostExecutionEvent() { }

    public sealed record Compiled(string BinaryPath) : HostExecutionEvent;
    public sealed record Stage(RunStage Value, string? Detail) : HostExecutionEvent;
    public sealed record Output(string Data, OutputKind Kind) : HostExecutionEvent;
    public sealed record InputRequested(string? Prompt, bool IsPassword) : HostExecutionEvent;
}

public sealed record HostExecutionResult(
    RunResult? RunResult,
    bool Cancelled,
    Exception? Error)
{
    public bool Succeeded => Error is null && !Cancelled && (RunResult?.Succeeded ?? true);

    public static HostExecutionResult Completed(RunResult? result) => new(result, false, null);
    public static HostExecutionResult Canceled() => new(null, true, null);
    public static HostExecutionResult Failed(Exception error) => new(null, false, error);
}

