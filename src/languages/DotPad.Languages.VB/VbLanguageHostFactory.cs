using DotPad.Contracts;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.VB;

public sealed class VbLanguageHostFactory : ILanguageHostFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public VbLanguageHostFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public string Language => "vb";

    // No IntelliSense flag yet — VB tabs compile/run/debug but have no autocomplete/diagnostics/
    // quick-fixes (would need a VisualBasicIntelliSenseService against Microsoft.CodeAnalysis.VisualBasic).
    public LanguageHostCapabilities Capabilities =>
        LanguageHostCapabilities.Run |
        LanguageHostCapabilities.Debug |
        LanguageHostCapabilities.Breakpoints |
        LanguageHostCapabilities.VariableInspection |
        LanguageHostCapabilities.MemoryDump;

    // No VB snippets defined yet — same gap as IntelliSense above.
    public IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>();

    public ILanguageHost Create(ILanguageHostOptions? options = null)
    {
        var opts = options as VbLanguageHostOptions
            ?? throw new ArgumentException(
                $"Expected {nameof(VbLanguageHostOptions)}.", nameof(options));
        return new VbLanguageHost(opts, _loggerFactory);
    }
}
