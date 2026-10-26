using RunPad.Contracts.IntelliSense;

namespace RunPad.UserControls;

/// <summary>Owns the four push subscriptions from one IntelliSense service to one editor.</summary>
internal sealed class IntelliSenseEventSubscriptions : IDisposable
{
    private readonly IIntelliSenseService _service;
    private readonly Action<string, IReadOnlyList<IDiagnosticInfo>> _diagnostics;
    private readonly Action<string, IReadOnlyList<ISemanticToken>> _semanticTokens;
    private readonly Action<string, IReadOnlyList<ICodeFixInfo>> _codeFixes;
    private readonly Action<string, IReadOnlyList<IInlayHint>> _inlayHints;
    private bool _disposed;

    public IntelliSenseEventSubscriptions(
        IIntelliSenseService service,
        Action<string, IReadOnlyList<IDiagnosticInfo>> diagnostics,
        Action<string, IReadOnlyList<ISemanticToken>> semanticTokens,
        Action<string, IReadOnlyList<ICodeFixInfo>> codeFixes,
        Action<string, IReadOnlyList<IInlayHint>> inlayHints)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(semanticTokens);
        ArgumentNullException.ThrowIfNull(codeFixes);
        ArgumentNullException.ThrowIfNull(inlayHints);
        _service = service;
        _diagnostics = diagnostics;
        _semanticTokens = semanticTokens;
        _codeFixes = codeFixes;
        _inlayHints = inlayHints;
        _service.DiagnosticsChanged += _diagnostics;
        _service.SemanticTokensChanged += _semanticTokens;
        _service.CodeFixesChanged += _codeFixes;
        _service.InlayHintsChanged += _inlayHints;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.DiagnosticsChanged -= _diagnostics;
        _service.SemanticTokensChanged -= _semanticTokens;
        _service.CodeFixesChanged -= _codeFixes;
        _service.InlayHintsChanged -= _inlayHints;
    }
}
