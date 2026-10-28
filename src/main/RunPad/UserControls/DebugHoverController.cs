using RunPad.Contracts.Models;

namespace RunPad.UserControls;

internal sealed class DebugHoverController : IDisposable
{
    public const int MaxChildren = 100;
    // Hovering evaluates on its own, so it gives up fast; expanding a member is asked for, so it waits longer.
    public static readonly TimeSpan EvaluateTimeout = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(3);
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;

    public (long Generation, CancellationToken Token) Begin()
    {
        if (_disposed) return (++_generation, new CancellationToken(canceled: true));
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        return (++_generation, _pending.Token);
    }

    public void Cancel()
    {
        if (_disposed)
        {
            _generation++;
            return;
        }
        _pending?.Cancel();
        _pending = null;
        _generation++;
    }

    public bool IsCurrent(long generation) => generation == _generation && !(_pending?.IsCancellationRequested ?? true);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
        _generation++;
    }

    public async Task<IReadOnlyList<IVariableInfo>> LoadChildrenAsync(IBreakpointHitContext context,
        IVariableInfo variable, CancellationToken cancellationToken)
    {
        if (variable.VariablesReference == 0) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChildTimeout);
        var children = await context.GetChildrenAsync(variable.VariablesReference, timeout.Token).ConfigureAwait(false);
        return children.Take(MaxChildren).ToArray();
    }

    public async Task<IVariableInfo?> EvaluateHoverAsync(IBreakpointHitContext context, string expression,
        CancellationToken cancellationToken)
    {
        if (!IsSafeHoverExpression(expression)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EvaluateTimeout);
        return await context.EvaluateAsync(expression, timeout.Token).ConfigureAwait(false);
    }

    public static bool IsSafeHoverExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var parts = expression.Split('.');
        return parts.All(part => part.Length > 0 &&
            (char.IsLetter(part[0]) || part[0] == '_') &&
            part.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_'));
    }

    public static IReadOnlyList<IVariableInfo>? Capture(IBreakpointHitContext? context)
        => context?.LocalVariables.ToArray();

    public static IVariableInfo? ResolveSnapshot(IReadOnlyList<IVariableInfo>? locals, string? symbol)
    {
        if (locals is null || string.IsNullOrEmpty(symbol)) return null;
        var matches = locals.Where(v => !IsSynthetic(v.Name)
            && string.Equals(v.Name, symbol, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsSynthetic(string name) => name is "Locals" or "Arguments" or "Registers" || string.IsNullOrWhiteSpace(name);
}
