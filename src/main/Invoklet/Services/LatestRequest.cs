namespace Invoklet.Services;

/// <summary>
/// The newest of a series of requests of one kind (a search, the versions of a package): a new request cancels the one
/// before it and gives the answer of an older one no standing, even when the service it asked ignores its token. Every
/// request carries the generation it began in and asks <see cref="IsCurrent"/> before it acts on its answer. Used on
/// the UI thread, where requests begin; the generation may be read from any thread.
/// </summary>
internal sealed class LatestRequest : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private int _generation;

    /// <summary>Cancels the request in flight, if any, and starts a new generation; returns it.</summary>
    public int Invalidate()
    {
        Release();
        return Interlocked.Increment(ref _generation);
    }

    /// <summary>The token of the request of the current generation, to be asked for after <see cref="Invalidate"/>.</summary>
    public CancellationToken NewToken()
    {
        _cancellation = new CancellationTokenSource();
        return _cancellation.Token;
    }

    public bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);

    /// <summary>Cancels the request in flight; the generation stays, so its answer still counts as current if it arrives.</summary>
    public void Dispose() => Release();

    private void Release()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }
}
