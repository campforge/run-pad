using Invoklet.Contracts;
using Invoklet.Contracts.Models;

namespace Invoklet.Services;

/// <summary>Adapts one host invocation to the UI-independent event/result model.</summary>
public static class HostExecutionAdapter
{
    public static Task<HostExecutionResult> RunAsync(
        ILanguageHost host,
        string script,
        Action<HostExecutionEvent> publish,
        CancellationToken cancellationToken) => ExecuteAsync(
            host, script, async (output, token) => (RunResult?)await host.RunAsync(script, output, token), publish, cancellationToken);

    public static Task<HostExecutionResult> DebugAsync(
        ILanguageHost host,
        string script,
        Action<HostExecutionEvent> publish,
        CancellationToken cancellationToken) => ExecuteAsync(
            host, script, async (output, token) =>
            {
                await host.RunDebugAsync(script, output, token).ConfigureAwait(false);
                return null;
            }, publish, cancellationToken);

    private static async Task<HostExecutionResult> ExecuteAsync(
        ILanguageHost host,
        string script,
        Func<OutputReceivedHandler, CancellationToken, Task<RunResult?>> invoke,
        Action<HostExecutionEvent> publish,
        CancellationToken cancellationToken)
    {
        // Hosts can raise an event concurrently with the invocation completing. Once the
        // invocation has returned, no event belongs to this execution anymore; otherwise a late
        // stage/input callback can reach a newer UI generation after the adapter has detached.
        var acceptingEvents = 1;
        void Publish(HostExecutionEvent @event)
        {
            if (Volatile.Read(ref acceptingEvents) != 0) publish(@event);
        }
        CompiledHandler compiled = path => Publish(new HostExecutionEvent.Compiled(path));
        StageChangedHandler stage = (value, detail) => Publish(new HostExecutionEvent.Stage(value, detail));
        InputRequestedHandler input = (prompt, password) => Publish(new HostExecutionEvent.InputRequested(prompt, password));
        host.Compiled += compiled;
        host.StageChanged += stage;
        host.InputRequested += input;
        try
        {
            var result = await invoke(
                (data, kind) => Publish(new HostExecutionEvent.Output(data, kind)), cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref acceptingEvents, 0);
            // A host may return successfully after cancellation was requested. Treat that as
            // cancellation rather than reporting a false successful run.
            return cancellationToken.IsCancellationRequested
                ? HostExecutionResult.Canceled()
                : HostExecutionResult.Completed(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref acceptingEvents, 0);
            return HostExecutionResult.Canceled();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref acceptingEvents, 0);
            return HostExecutionResult.Failed(ex);
        }
        finally
        {
            host.Compiled -= compiled;
            host.StageChanged -= stage;
            host.InputRequested -= input;
        }
    }
}
