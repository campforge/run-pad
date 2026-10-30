using Invoklet.Contracts.Models;
using Invoklet.UserControls.Models;

namespace Invoklet.Services;

/// <summary>
/// Evaluates the watch expressions of a tab against the debugger stop that is current. A new evaluation of an
/// expression cancels the one still running for it; a result that arrives after the stop moved on, the row was
/// removed or the evaluation was cancelled is dropped. Used on the UI thread.
/// </summary>
internal sealed class WatchEvaluator(Func<IBreakpointHitContext?> currentContext, Func<WatchExpression, bool> isListed)
{
    private readonly Dictionary<WatchExpression, CancellationTokenSource> _running = [];

    public async Task EvaluateAsync(WatchExpression watch)
    {
        Cancel(watch);
        if (string.IsNullOrWhiteSpace(watch.Expression))
        {
            Show(watch, "?", "");
            return;
        }

        if (currentContext() is not { } context)
        {
            Show(watch, "<not paused>", "");
            return;
        }

        using var evaluation = new CancellationTokenSource();
        _running[watch] = evaluation;
        var token = evaluation.Token;
        try
        {
            var result = await context.EvaluateAsync(watch.Expression, token);
            if (IsStale(watch, context, token)) return;
            Show(watch, result?.Value ?? "<error>", result?.TypeName ?? "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsStale(watch, context, token)) return;
            Show(watch, $"<error: {ex.Message}>", "");
        }
        finally
        {
            // A newer evaluation of the same row has already replaced this entry: leave it be.
            if (_running.TryGetValue(watch, out var current) && ReferenceEquals(current, evaluation))
                _running.Remove(watch);
        }
    }

    public void Cancel(WatchExpression watch)
    {
        if (_running.Remove(watch, out var evaluation)) evaluation.Cancel();
    }

    public void CancelAll()
    {
        foreach (var evaluation in _running.Values) evaluation.Cancel();
        _running.Clear();
    }

    private bool IsStale(WatchExpression watch, IBreakpointHitContext context, CancellationToken token)
        => token.IsCancellationRequested || !ReferenceEquals(context, currentContext()) || !isListed(watch);

    private static void Show(WatchExpression watch, string value, string typeName)
    {
        watch.Value = value;
        watch.TypeName = typeName;
    }
}
