using RunPad.Contracts.Models;

namespace RunPad.UserControls;

internal sealed class DebugHoverController
{
    private CancellationTokenSource? _pending;
    private long _generation;

    public (long Generation, CancellationToken Token) Begin()
    {
        _pending?.Cancel();
        _pending = new CancellationTokenSource();
        return (++_generation, _pending.Token);
    }

    public void Cancel()
    {
        _pending?.Cancel();
        _pending = null;
        _generation++;
    }

    public bool IsCurrent(long generation) => generation == _generation && !(_pending?.IsCancellationRequested ?? true);

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
