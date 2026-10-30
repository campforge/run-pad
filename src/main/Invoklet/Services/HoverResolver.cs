using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.UserControls;

namespace Invoklet.Services;

/// <summary>Evaluates a symbol in the paused program; <see cref="DebugHoverController"/> bounds it in time and to safe expressions.</summary>
internal interface IDebugHoverSource
{
    Task<IVariableInfo?> EvaluateHoverAsync(IBreakpointHitContext context, string expression, CancellationToken cancellationToken);
}

/// <summary>What is under the pointer. Taken on the UI thread, so the resolver can work on any thread.</summary>
internal sealed record HoverRequest(
    string Text,
    int Offset,
    IReadOnlySet<char>? IdentifierChars,
    IBreakpointHitContext? DebugContext,
    IReadOnlyList<IVariableInfo>? DebugLocals,
    IReadOnlyList<IDiagnosticInfo> Diagnostics,
    Func<bool> IsDebugContextCurrent);

internal abstract record HoverResult;

/// <summary>The value of the symbol in the paused program.</summary>
internal sealed record DebugValueHover(string Symbol, int SymbolStart, IVariableInfo Value) : HoverResult;

internal sealed record DiagnosticHover(string Message) : HoverResult;

internal sealed record QuickInfoHover(IQuickInfoResult Info) : HoverResult;

/// <summary>
/// Decides what the tooltip of a hovered position shows: the value of the symbol while the program is paused
/// (from the locals taken at the stop, else evaluated for a plain identifier), else the diagnostic there,
/// else the language's Quick Info. Nothing when there is nothing to say.
/// </summary>
internal sealed class HoverResolver(IDebugHoverSource debug, IIntelliSenseService intelliSense)
{
    public async Task<HoverResult?> ResolveAsync(HoverRequest request, CancellationToken cancellationToken)
    {
        var symbol = DebugHoverSymbolExtractor.Extract(request.Text, request.Offset, request.IdentifierChars, out var symbolStart);
        var debugValue = DebugHoverController.ResolveSnapshot(request.DebugLocals, symbol);
        // The snapshot is preferred. Only the bounded identifier/dotted-token policy of the source may fall back
        // to evaluation; arbitrary editor text is never sent to the debugger.
        if (debugValue is null && request.DebugContext is { } context && symbol is not null && request.IsDebugContextCurrent())
            debugValue = await debug.EvaluateHoverAsync(context, symbol, cancellationToken);
        if (debugValue is not null)
            return symbol is null ? null : new DebugValueHover(symbol, symbolStart, debugValue);

        // A diagnostic takes priority over Quick Info: why a squiggle is there matters more than the signature.
        var diagnostic = request.Diagnostics.FirstOrDefault(d => request.Offset >= d.StartOffset && request.Offset < d.EndOffset);
        if (diagnostic is not null) return new DiagnosticHover(diagnostic.Message);

        return await intelliSense.GetQuickInfoAsync(request.Text, request.Offset, cancellationToken) is { } info
            ? new QuickInfoHover(info)
            : null;
    }
}
