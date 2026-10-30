namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>
/// Runs the background analysis that follows an edit: it waits out a debounce, asks for the text only then (a notification
/// superseded by the next keystroke never builds its string), analyses it, and starts again on the same text when the analysis
/// reports that what it was built on changed meanwhile. A newer notification cancels the pass in flight. It knows nothing of
/// Roslyn: what is analysed is the delegate it is given.
/// </summary>
/// <param name="debounce">Read at every notification, so it can be changed between two.</param>
/// <param name="run">Starts the pass; the service wraps it in the culture its Roslyn messages are produced in.</param>
/// <param name="onFailure">Told of what a pass threw, other than being cancelled; the scheduler goes on.</param>
/// <param name="stopTimeout">How long <see cref="Stop"/> waits for a pass that does not notice its cancellation.</param>
internal sealed class AnalysisScheduler(
    Func<TimeSpan> debounce, Func<Func<Task>, Task> run, Action<Exception> onFailure, TimeSpan stopTimeout)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private Task? _pass;

    /// <summary>The pass last started; for tests to await.</summary>
    internal Task? Pass
    {
        get
        {
            lock (_gate) return _pass;
        }
    }

    /// <param name="getCode">Reads the text of the editor; called once the debounce is over, on the thread of the pass.</param>
    /// <param name="analyze">
    /// Analyses the text and says whether its result is still valid. False (the project context changed during the pass) makes
    /// the scheduler analyse the same text again, unless a newer notification has taken over.
    /// </param>
    public void Notify(Func<string> getCode, Func<string, CancellationToken, Task<bool>> analyze)
    {
        CancellationToken token;
        lock (_gate)
        {
            // Not disposed: a superseded pass can still be past its checks of the token (inside a code fix provider that does not
            // poll it), and reading a disposed source throws. A source whose wait handle is never touched holds no unmanaged
            // resources, so the superseded one is left to the collector; Stop disposes the last one.
            _current?.Cancel();
            _current = new CancellationTokenSource();
            token = _current.Token;
            _pass = run(async () =>
            {
                try
                {
                    await Task.Delay(debounce(), token);
                    var code = getCode();
                    if (!await analyze(code, token) && !token.IsCancellationRequested) Notify(() => code, analyze);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    onFailure(ex);
                }
            });
        }
    }

    /// <summary>
    /// Cancels the pass in flight and waits for it, for at most the timeout: a pass that never checks its token must not be able
    /// to hang the end of the editor for ever; a result published after this returns would reach a service that is gone.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? source;
        Task? pass;
        lock (_gate)
        {
            source = _current;
            pass = _pass;
        }

        source?.Cancel();
        try
        {
            pass?.Wait(stopTimeout);
        }
        catch
        {
            // Already reported from inside the pass.
        }

        source?.Dispose();
    }
}
