namespace Invoklet.Services;

/// <summary>
/// Turns the spans that background analysis changes into as few redraws as possible: each update reports only the span whose
/// decorations changed, spans that arrive before the posted redraw runs are merged, and the redraw covers just their union
/// (nothing at all when the analysis changed nothing). A redraw of the whole view for every update rebuilt and recolored every
/// visible line twice per analysis.
/// </summary>
/// <param name="post">Runs an action later, on the thread that draws and after the updates already queued.</param>
/// <param name="textLength">The length of the text to draw, or null when there is nothing to draw (no document, or the tab is gone).</param>
/// <param name="redraw">Redraws the given offset and length of the text.</param>
internal sealed class RedrawCoalescer(Action<Action> post, Func<int?> textLength, Action<int, int> redraw)
{
    private (int Start, int End)? _pending;
    private bool _posted;

    /// <summary>How many redraws have run; for tests.</summary>
    public int RedrawsRun { get; private set; }

    /// <summary>The span of text whose decorations changed; null (nothing changed) requests nothing.</summary>
    public void Request((int Start, int End)? changed)
    {
        if (changed is not { } range) return;
        _pending = _pending is { } pending
            ? (Math.Min(pending.Start, range.Start), Math.Max(pending.End, range.End))
            : range;
        if (_posted) return;

        _posted = true;
        post(Run);
    }

    private void Run()
    {
        _posted = false;
        if (_pending is not { } union || textLength() is not { } length) return;
        _pending = null;
        var start = Math.Clamp(union.Start, 0, length);
        var end = Math.Clamp(union.End, start, length);
        redraw(start, Math.Max(end - start, 1)); // a hint is zero-length
        RedrawsRun++;
    }
}
