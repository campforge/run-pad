using RunPad.Contracts;
using RunPad.Contracts.Models;

namespace RunPad.Services;

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
        CompiledHandler compiled = path => publish(new HostExecutionEvent.Compiled(path));
        StageChangedHandler stage = (value, detail) => publish(new HostExecutionEvent.Stage(value, detail));
        InputRequestedHandler input = (prompt, password) => publish(new HostExecutionEvent.InputRequested(prompt, password));
        host.Compiled += compiled;
        host.StageChanged += stage;
        host.InputRequested += input;
        try
        {
            var result = await invoke(
                (data, kind) => publish(new HostExecutionEvent.Output(data, kind)), cancellationToken).ConfigureAwait(false);
            return HostExecutionResult.Completed(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HostExecutionResult.Canceled();
        }
        catch (Exception ex)
        {
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
