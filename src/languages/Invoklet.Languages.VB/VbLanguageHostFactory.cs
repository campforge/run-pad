using Invoklet.Contracts;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.DotnetCommon;
using Microsoft.Extensions.Logging;

namespace Invoklet.Languages.VB;

public sealed class VbLanguageHostFactory : ILanguageHostFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IRuntimeReferenceLocator _runtimeReferences;

    public VbLanguageHostFactory(ILoggerFactory loggerFactory, IRuntimeReferenceLocator? runtimeReferences = null)
    {
        _loggerFactory = loggerFactory;
        _runtimeReferences = runtimeReferences ?? new DotnetRuntimeReferenceLocator();
    }

    public string Language => "vb";
    public string DisplayName => "Visual Basic";

    // See VbLanguageHost.SupportedCapabilities — single source of truth.
    public LanguageHostCapabilities Capabilities => VbLanguageHost.SupportedCapabilities;

    // No VB snippets defined yet — same gap as IntelliSense above.
    public IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>();

    // VB's own lexical shape, not C#'s: blocks are `Sub`/`End Sub`, `If`/`End If`, `Class`/`End
    // Class` — no braces at all, so brace-match/auto-close/fold only make sense for the parens and
    // array-literal brackets VB does share with C#. Generic type arguments use "Of", not a bare '<'
    // (which is also VB's comparison operator, same reasoning C# excludes it from
    // CompletionTriggers). No required statement terminator — a newline ends a statement. VB is
    // case-insensitive, so "New"/"new" both starting completion is a real language property, not
    // sloppiness, hence the case-insensitive HashSet.
    public string? CodeForKind(string code, RpadKind kind, string indent = "    ") => VbProgramTemplate.CodeForKind(code, kind, indent);

    public EditorLanguageProfile EditorProfile { get; } = new()
    {
        CompletionTriggers = new HashSet<char> { '.' },
        OverloadTriggers = new HashSet<char> { '(', ',' },
        IdentifierChars = new HashSet<char> { '_' },
        ContextualTriggers = new HashSet<char>(),
        WordTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "New" },
        StatementTerminators = new HashSet<char>(),
        BracePairs = new Dictionary<char, char> { ['('] = ')', ['['] = ']' },
        AutoCloseBraces = new HashSet<char> { '[' },
        LineCommentPrefix = "'",
        IndentationStrategy = IndentationStrategyKind.Default,
        // VB's blocks aren't brace-delimited, so the default brace-based
        // folding strategy folded nothing at all in a .vb file. VbFoldingRegions.Compute lives right
        // here in the VB language project, not in the language-agnostic editor control.
        FoldingRegionsProvider = VbFoldingRegions.Compute,
        // File tree's per-language icon. No Visual Basic-specific icon
        // exists in Material Design Icons (checked the library's own meta.json — no icon
        // anywhere named/tagged/aliased "visual-basic"/"vb.net"/"vbnet"/"basic"). Stand-in chosen
        // by the user: the Visual Studio logo (pictogrammers.com/library/mdi/icon/
        // microsoft-visual-studio) — VB.NET only exists inside Visual Studio, so it's a real, if
        // not per-language, association rather than an invented glyph.
        IconResourceKey = "Icon.LanguageVb",
        SourceFileExtension = VbProjectProfile.Instance.SourceFileExtension,
    };

    public CompiledDotnetLanguageHostOptions CreateOptions(ExecutionRequest request)
        => CompiledDotnetLanguageHostOptions.From(request, _runtimeReferences.GetRuntimeAssemblyPath());

    public ILanguageHost Create(ExecutionRequest request) => Create(CreateOptions(request));

    public ILanguageHost Create(ILanguageHostOptions? options = null)
    {
        var opts = options as CompiledDotnetLanguageHostOptions
            ?? throw new ArgumentException(
                $"Expected {nameof(CompiledDotnetLanguageHostOptions)}.", nameof(options));
        return new VbLanguageHost(opts, _loggerFactory);
    }
}
