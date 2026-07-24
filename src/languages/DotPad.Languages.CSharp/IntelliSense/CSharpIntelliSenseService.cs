using System.Collections.Concurrent;
using System.Text;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ContractSeverity = DotPad.Contracts.IntelliSense.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace DotPad.Languages.CSharp.IntelliSense;

public sealed class CSharpIntelliSenseService : IIntelliSenseService
{
    // Default usings the synthetic script document is built with, so completion/diagnostics work
    // without the user typing them — must match the compiler's own implicit usings exactly (see
    // ImplicitUsings.Default / ANALYSIS.md #27), otherwise IntelliSense shows no error for something
    // that won't actually compile. Extended at runtime with the .rpad front matter's own
    // `namespaces:` list (see UpdateGlobalUsings) so IntelliSense agrees with what the compiler
    // will actually add as `global using` too — see docs/ImplementationPlanRPad.md and RoslynCodeFixEngine.
    private static readonly string[] _basePreambleUsings = ImplicitUsings.Default;

    // Also used for signature-help method/parameter signatures and quick info, not just completion —
    // IncludeParamsRefOut/IncludeDefaultValue make those read like a real signature (`params object[]
    // args`, `string? label = null`) instead of just bare "type name" pairs.
    private static readonly SymbolDisplayFormat _briefFormat =
        new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // Matches Roslyn's own (internal) AbstractInlineTypeHintsService.s_minimalTypeStyle exactly —
    // decompiled via ICSharpCode.Decompiler (already a dependency, see CSharpDecompilerService) to
    // confirm rather than guess, since VS/Rider's own inlay-hint formatting isn't itself public API.
    private static readonly SymbolDisplayFormat _inlayHintTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.None,
        delegateStyle: SymbolDisplayDelegateStyle.NameOnly,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.Default,
        parameterOptions: SymbolDisplayParameterOptions.None,
        propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
        localOptions: SymbolDisplayLocalOptions.None,
        kindOptions: SymbolDisplayKindOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.AllowDefaultLiteral);

    private static readonly IReadOnlyList<ICompletionItem> _keywords =
        new[]
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while", "async", "await", "var", "dynamic", "yield", "get", "set",
            "add", "remove", "partial", "where", "select", "from", "let", "orderby", "group", "join",
            "into", "ascending", "descending", "on", "equals", "by", "when", "init", "record",
            "with", "not", "and", "or", "nint", "nuint", "file", "scoped", "required", "global",
            "nameof", "value", "unmanaged", "notnull",
        }
        // Rank 9 — see ScopeRank: keywords sort after every real symbol, so a local/parameter/member
        // that happens to fuzzy-match the same letters (see ANALYSIS.md TODO #51 follow-up) doesn't
        // lose a tie-break to a keyword the user isn't actually reaching for. Priority matches —
        // lowest of all kinds, see ScopeRankPriority.
        .Select(kw => (ICompletionItem)new SimpleCompletionItem(
            kw, CompletionItemKind.Keyword, [new DisplayTextSegment(kw, SemanticTokenKind.Keyword)],
            Summary: null, SortText: $"9{kw}", Priority: 0.1, UsingsToAdd: []))
        .ToArray();

    // TODO.md #59 remainder — contextual keywords with no legal meaning outside a specific syntax
    // position were being offered everywhere (e.g. "orderby"/"equals" inside an ordinary method body).
    // `where` is deliberately NOT included here — it's also the generic type-constraint clause keyword
    // (`where T : class`), which is legal in an ordinary member-declaration position, so filtering it
    // to "inside a query expression" would hide it somewhere it's actually valid.
    private static readonly HashSet<string> _queryContinuationKeywords = new(StringComparer.Ordinal)
    {
        "from", "join", "into", "let", "orderby", "select", "group", "on", "equals",
        "ascending", "descending", "by",
    };

    // get/set/add/remove only mean something inside a property/indexer/event accessor list.
    // `value` only means something inside a set/add/remove accessor body (the implicit parameter name).
    private static readonly HashSet<string> _accessorKeywords = new(StringComparer.Ordinal)
    {
        "get", "set", "add", "remove",
    };

    // TODO.md #78: the only keywords that are ever legal as a generic type argument are the predefined
    // type keywords (`List<int>`, `List<string>`, ...) plus `dynamic`/`nint`/`nuint` — every other
    // keyword in _keywords (control-flow, modifiers, LINQ, ...) is a syntax error there. `var`/`void`
    // are deliberately excluded: neither is a legal type argument (`List<var>`/`List<void>` don't
    // compile), unlike ordinary local-variable/return-type position where they're normally offered.
    private static readonly HashSet<string> _typeArgumentKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long", "nint",
        "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort",
    };

    private static bool IsKeywordApplicable(ICompletionItem keyword, SyntaxNode root, int position, bool isTypeArgumentPosition)
    {
        if (isTypeArgumentPosition)
            return _typeArgumentKeywords.Contains(keyword.DisplayText);

        if (_queryContinuationKeywords.Contains(keyword.DisplayText))
            return IsInsideQueryExpression(root, position);

        if (keyword.DisplayText == "value")
            return HasAncestorMatching(root, position, n =>
                n is AccessorDeclarationSyntax a && a.Kind() is SyntaxKind.SetAccessorDeclaration
                    or SyntaxKind.AddAccessorDeclaration or SyntaxKind.RemoveAccessorDeclaration);

        if (_accessorKeywords.Contains(keyword.DisplayText))
            return IsInsideAccessorContext(root, position);

        return true;
    }

    // TODO.md #78: true anywhere inside a generic type argument list (`List<`, `List<Str`,
    // `Dictionary<string, `, ...) — the only place where completion should be restricted to type-ish
    // symbols instead of every in-scope symbol. Reuses the same HasAncestorMatching position-1
    // fallback as every other "what syntax is the caret inside" check in this file (see
    // IsInsideQueryExpression/IsInsideAccessorContext above) since the caret while typing sits at a
    // token's end, not inside it.
    private static bool IsInsideTypeArgumentList(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is TypeArgumentListSyntax);

    private static readonly string[] _tfmPreference =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0",
        "netcoreapp3.1", "netstandard2.1", "netstandard2.0",
    ];

    // Debounce for NotifyTextChanged's background diagnostics/semantic-tokens pass — long enough
    // that fast typing doesn't re-run the full Roslyn analysis on every keystroke, short enough to
    // still feel live (ANALYSIS.md #94).
    private static readonly TimeSpan _backgroundAnalysisDebounce = TimeSpan.FromMilliseconds(500);

    // How long Dispose waits for an in-flight background analysis pass to actually observe
    // cancellation before giving up — see Dispose's own comment for why this is bounded rather than
    // unbounded.
    private static readonly TimeSpan _backgroundAnalysisDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<CSharpIntelliSenseService> _logger;
    private readonly AdhocWorkspace _workspace;
    private readonly DocumentId _documentId;

    // Test-only visibility (InternalsVisibleTo) — confirms the FormattingOptions.NewLine override
    // below actually takes effect on the workspace's live option set. Environment.NewLine can't be
    // forced to '\r\n' on a non-Windows dev/CI machine, so this is the closest local check for the
    // fix without a real Windows run.
    internal string FormattingNewLineOptionForTests
        => _workspace.CurrentSolution.Options.GetOption(FormattingOptions.NewLine, LanguageNames.CSharp);
    private readonly string _nugetRoot = NuGetPaths.GetGlobalPackagesFolder();
    private readonly List<MetadataReference> _nugetRefs = [];
    private readonly RoslynCodeFixEngine _codeFixEngine;
    private readonly RoslynAnalyzerEngine _analyzerEngine;
    private string[] _extraPreambleUsings = [];
    private string _preamble = "";
    private int _preambleLength;
    private RpadKind _kind = RpadKind.Statements;
    private string _lastCode = "";

    // TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход): the full synced document
    // text (preamble + KindPrefix + code + TrailingPad) and the live SourceText it produced, kept
    // alongside _lastCode so SyncDocument can hand Roslyn a minimal TextChange (ComputeMinimalChange)
    // against _lastSourceText instead of rebuilding the whole document from a fresh string on every
    // keystroke. SourceText.WithChanges preserves lineage to the old SourceText, letting Roslyn's
    // incremental lexer/parser reuse syntax nodes outside the edited span — benchmarked at ~7x faster
    // parsing and ~2-3x faster completion on 200-1000 line scripts versus SourceText.From every time
    // (full semantic diagnostics don't benefit the same way — GetDiagnostics is binding-dominated, not
    // parsing-dominated — see RoslynCodeFixEngine's own class doc comment for the bigger win that
    // targeted). _lastSourceText is also reset to null (forcing one full SourceText.From rebuild) in
    // UpdateGlobalUsings/UpdateKind alongside their own _lastCode = "" reset: both change _preamble or
    // _kind, which feed directly into newFullText, so the cached _lastFullText baseline would no
    // longer describe what the workspace's document is about to hold, and diffing against it would
    // compute a TextChange against the wrong text. UpdatePackageReferences's own _lastCode = "" reset
    // deliberately does NOT clear these two — it only swaps MetadataReferences (symbol resolution),
    // never the document's text, so the cached baseline is still accurate there and reusing it (a
    // no-op WithChanges diff) is strictly cheaper than forcing a full reparse for no reason.
    private string _lastFullText = "";
    private SourceText? _lastSourceText;

    // Test-only observability (InternalsVisibleTo), same "internal test hook" pattern as
    // CodeFixMargin.Fixes — lets a test confirm SyncDocument actually took the incremental
    // WithChanges path (SourceText lineage preserved across calls) rather than silently falling back
    // to SourceText.From every time, via SourceText.GetChangeRanges(older) which throws unless the two
    // instances share a lineage.
    internal SourceText? LastSyncedSourceTextForTests => _lastSourceText;

    // Guards _lastCode and the AdhocWorkspace's CurrentSolution against concurrent SyncDocument
    // calls — completion/signature-help/quick-info/background-analysis each run on their own
    // Task.Run, so without this a completion request for the text at keystroke N could race a
    // still-in-flight background-analysis pass for keystroke N-2 (ANALYSIS.md TODO #56).
    private readonly object _syncLock = new();
    private CancellationTokenSource? _diagnosticsCts;

    // Tracks NotifyTextChanged's own fire-and-forget Task.Run so Dispose can actually wait for it —
    // see Dispose's own comment for why this matters beyond just "clean shutdown".
    private Task? _backgroundAnalysisTask;

    // Shared across every CSharpIntelliSenseService instance (one per tab, see EditorTabsControl.
    // CreateControlAsync) — MetadataReferences are immutable and safe to share across unrelated
    // workspaces/compilations, so without this every new tab redid the PE-header + XML-doc parsing
    // for the same ~167 default BCL reference assemblies from scratch (ANALYSIS.md TODO #56).
    internal static readonly ConcurrentDictionary<string, MetadataReference> ReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    // See ReferenceDocumentation — without this, GetDocumentationCommentXml() comes back empty for
    // every metadata symbol (BCL, NuGet), so TODO.md #50's Summary was silently null for every
    // completion/signature-help/quick-info item except symbols the script itself defines.
    private static MetadataReference CreateReference(string path) =>
        ReferenceCache.GetOrAdd(path,
            static p => MetadataReference.CreateFromFile(p, documentation: ReferenceDocumentation.Find(p)));

    public CSharpIntelliSenseService(IEnumerable<string> referencePaths, ILogger<CSharpIntelliSenseService> logger)
    {
        _logger = logger;
        RebuildPreamble();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();
        foreach (var path in referencePaths)
            if (File.Exists(path) && seen.Add(path))
                refs.Add(CreateReference(path));

        _workspace = new AdhocWorkspace(DotPadRoslynHost.HostServices);

        // Root cause of a Windows-CI-only bug (chased down via extensive logging — see
        // RoslynCodeFixEngine's own comments): _preamble/KindPrefix/TrailingPad are all built with
        // literal '\n' regardless of OS, but Roslyn's own FormattingOptions.NewLine defaults to
        // Environment.NewLine ('\r\n' on Windows, '\n' on Linux/macOS) whenever a workspace doesn't
        // set it explicitly. A code fix that reformats whitespace it touches (RemoveUnusedVariable
        // normalizing the blank line before the statement it deletes, observed in production) then
        // emits '\r\n' where the document only ever had '\n' — on Windows this makes the fix's edit
        // span start earlier than expected (each extra newline byte shifts it back one more),
        // straddling across preambleLength by just enough to trip RoslynCodeFixEngine.
        // ApplyRelocatingPreambleEdits' "any preamble-touching edit that isn't a pure `using X;`
        // insertion is unsafe, discard the whole fix" safety check — so the fix silently vanishes
        // instead of applying, string-identical logic and all, depending only on which OS happens to
        // be running it. Forcing '\n' here makes Roslyn's own formatting match what this document
        // actually contains everywhere, independent of OS.
        _workspace.TryApplyChanges(_workspace.CurrentSolution.WithOptions(
            _workspace.CurrentSolution.Options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n")));

        _codeFixEngine = new RoslynCodeFixEngine(DotPadRoslynHost.CompositionHost, logger);
        _analyzerEngine = new RoslynAnalyzerEngine(logger);

        var projectId = ProjectId.CreateNewId("DotPadScript");
        _documentId = DocumentId.CreateNewId(projectId, "Script.cs");

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name: "DotPadScript",
            assemblyName: "DotPadScript",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: refs);

        _workspace.AddProject(projectInfo);
        _workspace.AddDocument(DocumentInfo.Create(
            _documentId,
            "Script.cs",
            loader: TextLoader.From(TextAndVersion.Create(
                SourceText.From(_preamble, Encoding.UTF8),
                VersionStamp.Create()))));
    }

    private void RebuildPreamble()
    {
        var sb = new StringBuilder();
        foreach (var u in _basePreambleUsings) sb.Append("using ").Append(u).Append(";\n");
        foreach (var u in _extraPreambleUsings) sb.Append("using ").Append(u).Append(";\n");
        sb.Append('\n');
        _preamble = sb.ToString();
        _preambleLength = _preamble.Length;
    }

    public void UpdateGlobalUsings(IReadOnlyList<string> usings)
    {
        var extra = usings
            .Where(u => !_basePreambleUsings.Contains(u, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (extra.SequenceEqual(_extraPreambleUsings, StringComparer.Ordinal)) return;

        _extraPreambleUsings = extra;
        RebuildPreamble();
        // force re-sync with the new preamble on the next analysis pass — also drop the cached
        // _lastSourceText/_lastFullText baseline (see their own doc comment): the preamble just
        // changed underneath it, so diffing against it would compute a TextChange against text the
        // workspace no longer holds.
        lock (_syncLock) { _lastCode = ""; _lastFullText = ""; _lastSourceText = null; }
    }

    public void UpdateKind(RpadKind kind)
    {
        if (_kind == kind) return;

        _kind = kind;
        // force re-sync with the new kind-transform prefix on the next analysis pass — same
        // baseline-invalidation reasoning as UpdateGlobalUsings above.
        lock (_syncLock) { _lastCode = ""; _lastFullText = ""; _lastSourceText = null; }
    }

    // The prefix ApplyKindTransform (CSharpLanguageHost) inserts before the user's code for
    // RpadKind.Program — mirrored here so the live-analysis document contains the same wrapper-class
    // call the compiler will actually emit, otherwise Roslyn correctly (from its own point of view)
    // flags the user's Main as an unused local function (CS8321). Deliberately NOT mirroring
    // Expression's transform here — it also trims/wraps the *end* of the code, which would desync the
    // position math below (HeaderLength only accounts for a prefix, not a rewritten/shortened body).
    // Code-dependent since ProgramPrefixFor picks the call-statement shape based on whether the user's
    // own Main is static and/or Task-returning (TODO.md #63, #107) — every call site already has
    // `code` in scope (it's what's being analyzed), so this just threads it through.
    private string KindPrefix(string code) => _kind == RpadKind.Program ? RpadKindTransform.ProgramPrefixFor(code) : "";

    // Closes the wrapper class KindPrefix opens for RpadKind.Program (TODO.md #107) — mirrored for the
    // same reason as the prefix: without it, the live-analysis document would contain a permanently
    // unclosed class declaration, breaking parsing/binding for the rest of the synced text.
    // Code-dependent for the same reason as KindPrefix — ProgramSuffixFor's synthetic trampoline shape
    // depends on whether the user's own Main is static/Task-returning.
    private string KindSuffix(string code) => _kind == RpadKind.Program ? RpadKindTransform.ProgramSuffixFor(code) : "";

    // Total offset from the start of the live-analysis document text to the start of the user's own
    // code — usings preamble plus (for RpadKind.Program) the mirrored wrapper-class prefix above.
    private int HeaderLength(string code) => _preambleLength + KindPrefix(code).Length;

    // TODO.md #77: with nothing after the user's own code, a query position sitting at the very end
    // of it (e.g. right after typing a fresh single-letter identifier at the end of the last line —
    // the exact reported repro: `var a = ...;` then `a` on its own line, nothing typed after it yet)
    // coincides with the true end of the whole synced document. Roslyn's SemanticModel.LookupSymbols
    // silently stops finding block-scoped locals/parameters right at that exact boundary — confirmed
    // directly against Roslyn (position == EndOfFileToken, whose parent is the bare CompilationUnit
    // rather than any enclosing statement) — so the just-declared local `a` dropped out of the
    // completion list entirely, leaving the implicit top-level-statement `args` parameter (which
    // isn't affected by this boundary — it resolves from a different binder chain) as the only
    // remaining "a"-prefixed candidate. A single trailing space, appended once here, moves every
    // legitimate query position (which can only ever be <= HeaderLength + code.Length) strictly
    // before the document's new true end, sidestepping the boundary entirely — confirmed empirically
    // to restore the local for that same repro. Purely whitespace, so it can't itself introduce new
    // diagnostics/tokens; RoslynCodeFixEngine is the one consumer that reconstructs "the user's code"
    // by measuring from the document's end rather than from the known userCode.Length, so it takes an
    // explicit adjustment (see ApplyRelocatingPreambleEdits) to not leak this pad into a fix's output.
    private const string TrailingPad = " ";

    // ── Document sync ──────────────────────────────────────────────────────────

    private Document SyncDocument(string code)
    {
        lock (_syncLock)
        {
            if (_lastCode != code)
            {
                var newFullText = _preamble + KindPrefix(code) + code + KindSuffix(code) + TrailingPad;

                // TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход): hand Roslyn a
                // minimal TextChange against the previous SourceText instead of always building a fresh
                // one from a full string — see _lastFullText/_lastSourceText's own doc comment for why
                // (incremental lexing/parsing reuse) and the measured impact. Only the very first sync,
                // or the first one after a forced reset (UpdateGlobalUsings/UpdateKind), falls back to a
                // full SourceText.From — there's no prior lineage to diff against at that point.
                var text = _lastSourceText is null
                    ? SourceText.From(newFullText, Encoding.UTF8)
                    : _lastSourceText.WithChanges(ComputeMinimalChange(_lastFullText, newFullText));

                var newSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, text);
                // Only remember the new code once the workspace actually reflects it — TryApplyChanges
                // can fail (e.g. a solution-version conflict from a concurrent update elsewhere), and
                // updating _lastCode unconditionally used to mean a failed apply left CurrentSolution
                // showing stale text while _lastCode claimed it was already in sync, permanently
                // skipping the sync on every later call with that same code.
                if (_workspace.TryApplyChanges(newSolution))
                {
                    _lastCode = code;
                    _lastFullText = newFullText;
                    _lastSourceText = text;
                }
            }
            return _workspace.CurrentSolution.GetDocument(_documentId)!;
        }
    }

    // Smallest single TextChange that turns oldText into newText: the common leading prefix and
    // trailing suffix are left untouched, and everything between them is replaced wholesale. Not a
    // real diff algorithm (no attempt to find a minimal edit script across multiple disjoint
    // changes) — deliberately so, since the one thing this needs to guarantee is "SourceText.WithChanges
    // applied to oldText reproduces newText exactly", and a single prefix/suffix-trimmed span already
    // does that for the actual call pattern here (one contiguous edit per keystroke — a typed
    // character, a paste, a delete — not simultaneous edits in unrelated parts of the document).
    // Internal (not private) + InternalsVisibleTo, same "internal test hook" pattern as
    // CodeFixMargin.WorstSeverityOnLine — lets ComputeMinimalChangeTests exercise the edit-span math
    // directly against edge cases (identical text, pure insert/delete, full replace, empty strings)
    // without needing to drive a real SyncDocument call through the whole AdhocWorkspace.
    internal static TextChange ComputeMinimalChange(string oldText, string newText)
    {
        var maxCommon = Math.Min(oldText.Length, newText.Length);

        var prefixLength = 0;
        while (prefixLength < maxCommon && oldText[prefixLength] == newText[prefixLength])
            prefixLength++;

        var maxSuffix = maxCommon - prefixLength;
        var suffixLength = 0;
        while (suffixLength < maxSuffix
            && oldText[oldText.Length - 1 - suffixLength] == newText[newText.Length - 1 - suffixLength])
            suffixLength++;

        var span = new TextSpan(prefixLength, oldText.Length - prefixLength - suffixLength);
        var replacement = newText.Substring(prefixLength, newText.Length - prefixLength - suffixLength);
        return new TextChange(span, replacement);
    }

    // ── Completion ─────────────────────────────────────────────────────────────

    public Task<ICompletionList?> GetCompletionsAsync(
        string code, int position, CompletionTrigger trigger = default, CancellationToken ct = default)
        => Task.Run(async () => (ICompletionList?)await TryGetCompletionsAsync(code, position, trigger, ct), ct);

    private async Task<ICompletionList?> TryGetCompletionsAsync(
        string code, int position, CompletionTrigger trigger, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = position + HeaderLength(code);

            // TODO.md item 60 point 8 — checked before the position guards below: "override" is a
            // reserved keyword that can only ever be a modifier, so if it's literally the word right
            // before the caret, this is unambiguously an override-stub request regardless of what the
            // (necessarily incomplete, not-yet-typed-past-it) surrounding syntax looks like to
            // Roslyn's error recovery — e.g. IsDeclarationNamePosition has no reason to fire here, but
            // there's no need to depend on that holding.
            if (IsAfterOverrideModifier(code, position))
            {
                var overrideType = GetEnclosingType(model, pos);
                if (overrideType?.BaseType is null) return null;

                var overrideItems = GetOverridableMethods(overrideType)
                    .Select(m => BuildOverrideStubItem(m, model, pos))
                    .OrderBy(i => i.SortText, StringComparer.Ordinal)
                    .ToList();
                return overrideItems.Count > 0 ? new SimpleCompletionList(overrideItems) : null;
            }

            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)
                || IsDeclarationNamePosition(root, pos))
                return null;

            var isDotInsertion = trigger.Kind == CompletionTriggerKind.Insertion && trigger.Character == '.';

            // An explicit Ctrl+Space invoke right after a '.' should behave like typing the dot
            // itself — member completion for the expression before it — not fall through to the
            // generic in-scope-symbols+keywords list below (which is where keywords like `for`/`if`
            // would otherwise show up sitting right next to `.Length`/`.Count`).
            if ((isDotInsertion || trigger.Kind == CompletionTriggerKind.Invoked) && pos > 0)
            {
                var token = root.FindToken(pos - 1);

                // x.Foo — ordinary member access. x?.Foo — same idea, but the '.' belongs to a
                // MemberBindingExpression hanging off a ConditionalAccessExpression instead (the
                // qualifier lives one level up, off the ConditionalAccessExpression itself, not the
                // MemberBindingExpression — which has no Expression of its own). Without this second
                // case, "s?." resolved to nothing at all (ANALYSIS.md #52).
                var memberAccess = token.Parent?.AncestorsAndSelf()
                    .OfType<MemberAccessExpressionSyntax>()
                    .FirstOrDefault();
                var memberBinding = token.Parent?.AncestorsAndSelf()
                    .OfType<MemberBindingExpressionSyntax>()
                    .FirstOrDefault();
                var qualifier = memberAccess?.Expression
                    ?? (memberBinding?.Parent as ConditionalAccessExpressionSyntax)?.Expression;

                if (qualifier != null)
                {
                    var qualifierSymbol = model.GetSymbolInfo(qualifier).Symbol;

                    // "System." — the qualifier itself names a namespace, not an expression with a
                    // value/type. GetTypeInfo has nothing for this (a namespace isn't a type), so
                    // without this branch it fell through to "nothing sensible to complete" and
                    // returned null (ANALYSIS.md #52) — LookupNamespacesAndTypes is the correct API
                    // for offering the namespace's nested namespaces/types instead.
                    if (qualifierSymbol is INamespaceSymbol containingNamespace)
                    {
                        var nsItems = GroupByDisplayText(model.LookupNamespacesAndTypes(pos, container: containingNamespace)
                                .Select(s => ToItem(s, currentType: null))
                                .Where(i => i != null)
                                .Cast<ICompletionItem>())
                            .OrderBy(i => i.SortText, StringComparer.Ordinal)
                            .ToList();

                        return nsItems.Count > 0 ? new SimpleCompletionList(nsItems) : null;
                    }

                    // "DateTime." (the qualifier names the type itself — a static-member access) vs.
                    // "d." (the qualifier is a value of that type — an instance access): GetSymbolInfo
                    // tells them apart (only the former resolves the qualifier to the INamedTypeSymbol
                    // itself), GetTypeInfo does not (a type used as a qualifier has no "type of its
                    // own" from GetTypeInfo's point of view, which is why the static case used to fall
                    // through to "nothing to complete" — ANALYSIS.md #52 static/instance mixing).
                    var isStaticQualifier = qualifierSymbol is INamedTypeSymbol;
                    ITypeSymbol? type;
                    if (isStaticQualifier)
                    {
                        type = (ITypeSymbol)qualifierSymbol!;
                    }
                    else
                    {
                        var typeInfo = model.GetTypeInfo(qualifier);
                        type = typeInfo.Type ?? typeInfo.ConvertedType;
                    }

                    if (type != null)
                    {
                        // LookupSymbols(container: type) returns every member visible in that type's
                        // scope regardless of static-ness — "DateTime." used to also offer AddDays
                        // (instance) and "d." used to also offer Parse (static), see ANALYSIS.md #52.
                        // Extension methods only make sense in the instance case (the static-qualifier
                        // "DateTime.SomeExtension()" call form isn't what LookupSymbols' reduced form
                        // represents), so they're only requested there too.
                        var memberItems = GroupByDisplayText(model
                                .LookupSymbols(pos, container: type, includeReducedExtensionMethods: !isStaticQualifier)
                                .Where(s => MatchesQualifierStaticness(s, isStaticQualifier))
                                .Select(s => ToItem(s, currentType: type))
                                .Where(i => i != null)
                                .Cast<ICompletionItem>())
                            .OrderBy(i => i.SortText, StringComparer.Ordinal)
                            .ToList();

                        return memberItems.Count > 0 ? new SimpleCompletionList(memberItems) : null;
                    }
                }

                // A typed '.' with nothing sensible to complete has nothing worth falling back to;
                // an explicit invoke elsewhere (not after a dot, or Invoked with no resolvable
                // member type) should still fall through to the general list below.
                if (isDotInsertion) return null;
            }

            // TODO.md #115 — live user request: `new Tests() { P1 = "1", | }` offered whatever's
            // visible in the enclosing scope (locals, fields of the CONTAINING type, ...) instead of
            // Tests' own remaining settable members, because nothing here ever checked for an
            // object-initializer position at all — it just fell through to the general LookupSymbols
            // fallback below like any other identifier position. Only the direct
            // "new Foo { X = 1, | }" shape (ObjectInitializerExpression whose own parent is the
            // object-creation expression) is handled — a nested initializer with no `new` of its own
            // ("new Outer { Inner = { | } }") and `with { }` expressions are a different, rarer shape
            // not covered here.
            if (TryFindObjectInitializer(root, pos) is { } initializer &&
                initializer.Parent is BaseObjectCreationExpressionSyntax objectCreation)
            {
                var initializerItems = BuildObjectInitializerCompletions(model, pos, objectCreation, initializer);
                return initializerItems.Count > 0 ? new SimpleCompletionList(initializerItems) : null;
            }

            var isTypeArgumentPosition = IsInsideTypeArgumentList(root, pos);

            // TODO.md #78 repro: `var a = new List<` unconditionally called LookupSymbols and offered
            // every visible symbol (locals, parameters, methods, ...) mixed in with actual types — this
            // narrows that to only what can legally appear as a generic type argument. Namespaces stay
            // in (not just named types/type parameters) so `List<Sys` can still offer "System" and let
            // the user descend into it with '.' to reach the type they actually want.
            IEnumerable<ISymbol> symbols = model.LookupSymbols(pos);
            if (isTypeArgumentPosition)
                symbols = symbols.Where(s => s is INamedTypeSymbol or ITypeParameterSymbol or INamespaceSymbol);

            var currentType = GetEnclosingType(model, pos);
            var visible = GroupByDisplayText(symbols
                .Select(s => ToItem(s, currentType))
                .Where(i => i != null)
                .Cast<ICompletionItem>()
                .Concat(_keywords.Where(k => IsKeywordApplicable(k, root, pos, isTypeArgumentPosition))));

            // Auto-import (TODO.md #28 remainder): types from an added NuGet package that aren't in
            // scope yet don't just fail silently — they're offered too, prefixed-matched against
            // whatever's typed so far, and accepting one adds its "using" automatically (see
            // EditorCompletionData.UsingsToAdd / EditorTabItemControl.AddGlobalUsings). Scoped to NuGet
            // references specifically, not the full BCL reference-assembly pack (~167 facades) —
            // walking every namespace of every default reference on every fresh identifier would
            // reintroduce the exact latency problem #56 just fixed, and BCL types the user actually
            // reaches for are already reachable through the default implicit usings anyway.
            var prefix = GetIdentifierPrefix(code, position);
            var alreadyVisible = new HashSet<string>(visible.Select(i => i.DisplayText), StringComparer.Ordinal);
            var importable = FindImportableTypes(model.Compilation, prefix, alreadyVisible)
                .Select(BuildImportableTypeItem)
                .ToList();

            var items = visible.Concat(importable)
                .OrderBy(i => i.SortText, StringComparer.Ordinal)
                .ToList();

            return items.Count > 0 ? new SimpleCompletionList(items) : null;
        }
        // A superseded request (see CodeTextEditor.ShowCompletion/ANALYSIS.md #57) cancels its token —
        // that's an expected outcome, not a failure, so it must propagate instead of being swallowed by
        // the general catch below (which would otherwise log it as a spurious warning on every fast
        // keystroke that supersedes an in-flight request).
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Completions failed"); return null; }
    }

    // Collapses same-named candidates down to one item per DisplayText (the same instance already
    // won by ScopeRank/Kind ordering everywhere this was called before), stamping the survivor with
    // OverloadCount when more than one Method-kind candidate shares the name (TODO.md #59 remainder —
    // without this, accepting a name with several overloads gave no hint there was anything else
    // under it at all) and with IsOwnMember if ANY grouped candidate is declared directly on the
    // current/receiver type (TODO.md item 60 point 7) — e.g. Derived.Foo(int) shadowing
    // Base.Foo(string) should still render bold even though ScopeRank/Kind ordering might pick the
    // base one as the group's representative.
    private static IReadOnlyList<ICompletionItem> GroupByDisplayText(IEnumerable<ICompletionItem> items) =>
        items
            .GroupBy(i => i.DisplayText)
            .Select(g =>
            {
                var chosen = g.OrderBy(i => (int)i.Kind).First();
                if (chosen is not SimpleCompletionItem simple) return chosen;

                var overloadCount = g.Count(i => i.Kind == CompletionItemKind.Method);
                var isOwnMember = g.Any(i => i.IsOwnMember);
                return simple with
                {
                    OverloadCount = overloadCount > 1 ? overloadCount : simple.OverloadCount,
                    IsOwnMember = isOwnMember,
                };
            })
            .ToList();

    // The INamedTypeSymbol containing the given position — used for the general (non-dot) completion
    // branch's "is this an own member" check (TODO.md item 60 point 7). Walks up from
    // GetEnclosingSymbol rather than assuming a fixed nesting depth, since top-level statements (this
    // project's primary script shape) land inside a compiler-synthesized Program class one level
    // deeper than an ordinary explicit class body would.
    private static INamedTypeSymbol? GetEnclosingType(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol is not null and not INamedTypeSymbol)
            symbol = symbol.ContainingSymbol;
        return symbol as INamedTypeSymbol;
    }

    private static bool IsOwnMember(ISymbol symbol, ITypeSymbol? currentType) =>
        currentType is not null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, currentType);

    // ── Override-member stubs (TODO.md item 60 point 8) ────────────────────────

    // True when the word immediately before position — skipping back over the trigger space itself —
    // is the whole word "override" (not e.g. "myoverride"). Deliberately a raw string scan, not a
    // syntax-tree check: at this position nothing has been typed for the member yet, so there's no
    // stable node to anchor a tree-based check to (same rationale as GetIdentifierPrefix elsewhere in
    // this file).
    private static bool IsAfterOverrideModifier(string code, int position)
    {
        var end = Math.Clamp(position, 0, code.Length);
        while (end > 0 && code[end - 1] is ' ' or '\t') end--;

        const string keyword = "override";
        if (end < keyword.Length) return false;
        var start = end - keyword.Length;
        if (!code.AsSpan(start, keyword.Length).SequenceEqual(keyword)) return false;

        return start == 0 || !(char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_');
    }

    // Every method in currentType's base chain that's still overridable — virtual, abstract, or a
    // non-sealed override further up (itself still a valid slot to override again) — deduplicated to
    // the most-derived occurrence of each signature (walking from BaseType upward, the first hit for
    // a given OriginalDefinition wins), and excluding anything currentType has already overridden
    // itself. A sealed override is excluded outright rather than falling through to a less-derived,
    // still-overridable declaration of the same signature — sealed really does mean "stop offering
    // this one here", not "offer the next-best thing instead".
    private static IEnumerable<IMethodSymbol> GetOverridableMethods(INamedTypeSymbol currentType)
    {
        var alreadyOverridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var own in currentType.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride))
            for (var overridden = own.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                alreadyOverridden.Add(overridden.OriginalDefinition);

        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        for (var type = currentType.BaseType; type != null; type = type.BaseType)
        {
            foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.MethodKind != MethodKind.Ordinary) continue;
                if (!m.IsVirtual && !m.IsAbstract && !m.IsOverride) continue;
                if (m.IsSealed) continue;
                if (!seen.Add(m.OriginalDefinition)) continue;
                if (alreadyOverridden.Contains(m.OriginalDefinition)) continue;

                yield return m;
            }
        }
    }

    // An override's accessibility must match the overridden member's exactly — C# doesn't allow
    // changing it — so this only ever needs to round-trip Accessibility back to the keyword(s) that
    // produced it, not handle every possible value (private/NotApplicable can't reach here: an
    // overridable member is never private).
    private static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Internal => "internal",
        _ => "protected",
    };

    private static string FormatOverrideParameter(IParameterSymbol p, SemanticModel model, int pos)
    {
        var refPrefix = p.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        var paramsPrefix = p.IsParams ? "params " : "";
        return $"{paramsPrefix}{refPrefix}{p.Type.ToMinimalDisplayString(model, pos)} {p.Name}";
    }

    private static string ForwardOverrideArgument(IParameterSymbol p) => p.RefKind switch
    {
        RefKind.Ref => $"ref {p.Name}",
        RefKind.Out => $"out {p.Name}",
        RefKind.In => $"in {p.Name}",
        _ => p.Name,
    };

    // The stub template — same "${0}" cursor-marker/tab-indent syntax CodeTextEditor.ExpandSnippet
    // already expands for the hand-authored Snippets dictionary (ILanguageHostFactory.Snippets), so
    // no new insertion mechanism is needed on the editor side. An abstract member has no base
    // implementation to call, so it gets a NotImplementedException stub instead — calling base.M(...)
    // there wouldn't compile.
    private static ICompletionItem BuildOverrideStubItem(IMethodSymbol method, SemanticModel model, int pos)
    {
        var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToMinimalDisplayString(model, pos);
        var parameters = string.Join(
            ", ", method.Parameters.Select(p => FormatOverrideParameter(p, model, pos)));
        var arguments = string.Join(", ", method.Parameters.Select(ForwardOverrideArgument));
        var call = method.IsAbstract
            ? "throw new NotImplementedException();"
            : method.ReturnsVoid
                ? $"base.{method.Name}({arguments});"
                : $"return base.{method.Name}({arguments});";

        var template =
            $"{AccessibilityKeyword(method.DeclaredAccessibility)} override {returnType} {method.Name}({parameters})\n" +
            "{\n" +
            $"\t{call}${{0}}\n" +
            "}";

        return new SimpleCompletionItem(
            method.Name, CompletionItemKind.Method,
            method.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(method),
            SortText: method.Name, Priority: 1.0, UsingsToAdd: [], InsertTemplate: template);
    }

    // TODO.md #115 — the nearest ObjectInitializerExpression ancestor of the token right before the
    // caret, same "walk up from the token before pos" idiom the dot/member-access branch above uses
    // for MemberAccessExpressionSyntax. Right after '{' or ',' (the common case — nothing typed yet
    // for the next member), that token's own Parent already IS the InitializerExpressionSyntax (open
    // brace and the SeparatedSyntaxList's separators are both direct children of it); mid-typing a
    // member name, the token's parent chain still leads up through it.
    private static InitializerExpressionSyntax? TryFindObjectInitializer(SyntaxNode root, int pos)
    {
        if (pos <= 0) return null;
        var token = root.FindToken(pos - 1);
        return token.Parent?.AncestorsAndSelf()
            .OfType<InitializerExpressionSyntax>()
            .FirstOrDefault(i => i.IsKind(SyntaxKind.ObjectInitializerExpression));
    }

    // GetTypeInfo on the object-creation EXPRESSION itself, not its .Type child node — same Roslyn
    // quirk noted in TryGetSignatureHelpAsync below: the latter comes back null for a type declared
    // in the same document being analyzed.
    private static List<ICompletionItem> BuildObjectInitializerCompletions(
        SemanticModel model, int pos, BaseObjectCreationExpressionSyntax objectCreation,
        InitializerExpressionSyntax initializer)
    {
        if (model.GetTypeInfo(objectCreation).Type is not INamedTypeSymbol type) return [];

        // Already-assigned members ("P1 = " already typed) shouldn't be offered again — LookupSymbols
        // has no idea what's already been set inside THIS particular initializer, only what's a
        // legal member of the type in general.
        var alreadyAssigned = new HashSet<string>(
            initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left).OfType<IdentifierNameSyntax>()
                .Select(id => id.Identifier.ValueText),
            StringComparer.Ordinal);

        // LookupSymbols(pos, container: type) — same API the dot-member-access branch above uses —
        // already filters to what's accessible from this position, so no separate accessibility check
        // is needed here. Narrowed further to actually-settable members: a property needs a setter
        // (SetMethod is non-null for both `set` and `init` accessors — `with`-only init-only members
        // are still legitimately settable from an object initializer), a field needs to not be
        // readonly/const, and neither can be static (an object initializer only ever targets instance
        // members of the freshly-created instance).
        var members = model.LookupSymbols(pos, container: type)
            .Where(s => s switch
            {
                IPropertySymbol { IsStatic: false, SetMethod: not null } => true,
                IFieldSymbol { IsStatic: false, IsReadOnly: false, IsConst: false } => true,
                _ => false,
            })
            .Where(s => !alreadyAssigned.Contains(s.Name))
            .Select(s => ToItem(s, currentType: type))
            .Where(i => i != null)
            .Cast<ICompletionItem>();

        return GroupByDisplayText(members).OrderBy(i => i.SortText, StringComparer.Ordinal).ToList();
    }

    private static ICompletionItem? ToItem(ISymbol symbol, ITypeSymbol? currentType) => symbol switch
    {
        // LocalFunction alongside Ordinary/ReducedExtension — top-level statements (this project's
        // main script shape) use local functions as their primary way to structure code, so leaving
        // them out meant the single most common non-trivial identifier in a script never completed
        // (ANALYSIS.md #52).
        IMethodSymbol m when m.MethodKind is MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.LocalFunction
            => BuildItem(m, m.Name, CompletionItemKind.Method, IsOwnMember(m, currentType)),
        IPropertySymbol p
            => BuildItem(p, p.Name, CompletionItemKind.Property, IsOwnMember(p, currentType)),
        IFieldSymbol f when !f.IsImplicitlyDeclared
            => BuildItem(f, f.Name,
                f.ContainingType?.TypeKind == TypeKind.Enum ? CompletionItemKind.EnumMember : CompletionItemKind.Field,
                IsOwnMember(f, currentType)),
        IEventSymbol e
            => BuildItem(e, e.Name, CompletionItemKind.Event, IsOwnMember(e, currentType)),
        ILocalSymbol l
            => BuildItem(l, l.Name, CompletionItemKind.Variable),
        IParameterSymbol p
            => BuildItem(p, p.Name, CompletionItemKind.Variable),
        INamedTypeSymbol t
            => BuildItem(t, t.Name, t.TypeKind switch
            {
                TypeKind.Interface => CompletionItemKind.Interface,
                TypeKind.Enum => CompletionItemKind.Enum,
                TypeKind.Struct => CompletionItemKind.Struct,
                TypeKind.Delegate => CompletionItemKind.Delegate,
                _ => CompletionItemKind.Class,
            }),
        // Type parameters (the `T` in `void Foo<T>(T value)`) were dropped entirely — inside a
        // generic method/type's own body, `T` is a real, commonly-referenced symbol (ANALYSIS.md
        // TODO #59), not just declaration-site syntax.
        ITypeParameterSymbol tp
            => BuildItem(tp, tp.Name, CompletionItemKind.TypeParameter),
        // Namespaces were silently dropped here despite ScopeRank already carrying a rank for them
        // (3 — below types) — LookupSymbols(pos) does return INamespaceSymbol candidates (e.g. typing
        // "Sys" offers "System" in real VS/Rider), they just never became a completion item
        // (ANALYSIS.md #52). CompletionItemKind has no dedicated "Namespace" entry — Module is the
        // closest LSP-style stand-in (VS Code/LSP itself uses Module for both).
        INamespaceSymbol ns
            => BuildItem(ns, ns.Name, CompletionItemKind.Module),
        _ => null
    };

    private static ICompletionItem BuildItem(
        ISymbol symbol, string displayText, CompletionItemKind kind, bool isOwnMember = false)
    {
        var rank = ScopeRank(symbol);
        return new SimpleCompletionItem(displayText, kind,
            symbol.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(symbol),
            SortText: $"{rank}{displayText}", Priority: ScopeRankPriority(rank), UsingsToAdd: [],
            IsOwnMember: isOwnMember);
    }

    // TODO.md #28 remainder / item 60 point 6 — a type from an added NuGet package that isn't in
    // scope yet. Own tier (ScopeRank 3, between ordinary types and namespaces) rather than sharing
    // rank 2 with already-visible types, and carries Detail (the containing namespace) so the
    // completion list itself shows the ReSharper-style "(in Namespace)" annotation — see
    // EditorTabItemControl's completion-item construction. UsingsToAdd is what tells
    // EditorTabItemControl to add the `using` automatically when this item is accepted (see
    // CompletionRequestedEventArgs's consumer in EditorTabItemControl.AsyncCompletionProvider).
    private static ICompletionItem BuildImportableTypeItem(INamedTypeSymbol type)
    {
        var kind = type.TypeKind switch
        {
            TypeKind.Interface => CompletionItemKind.Interface,
            TypeKind.Enum => CompletionItemKind.Enum,
            TypeKind.Struct => CompletionItemKind.Struct,
            TypeKind.Delegate => CompletionItemKind.Delegate,
            _ => CompletionItemKind.Class,
        };
        // A type in the global namespace needs no "using" to become visible — treat it as needing no
        // import rather than emitting an empty namespace, which would otherwise surface as "(in )" in
        // the list and add an empty entry to the .rpad front matter's namespaces (later producing an
        // invalid "using ;").
        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();

        const int rank = 3; // own tier, see ScopeRank
        return new SimpleCompletionItem(
            type.Name, kind, type.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(type),
            SortText: $"{rank}{type.Name}", Priority: ScopeRankPriority(rank),
            UsingsToAdd: ns is null ? [] : [ns], Detail: ns);
    }

    // Types from added NuGet references (not the full BCL reference pack — see the call site) whose
    // simple name starts with what's typed so far and isn't already visible. Candidates that collide
    // on name — either with something already in scope, or with each other (two different packages
    // exporting the same type name) — are dropped entirely rather than guessing which one the user
    // meant; offering the wrong one and silently adding the wrong `using` would be worse than not
    // offering it at all.
    private IEnumerable<INamedTypeSymbol> FindImportableTypes(
        Compilation compilation, string prefix, HashSet<string> alreadyVisible)
    {
        if (prefix.Length == 0) yield break;

        var byName = new Dictionary<string, INamedTypeSymbol?>(StringComparer.Ordinal);
        foreach (var reference in _nugetRefs)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;

            foreach (var entry in GetImportableTypeIndex(assembly, reference))
            {
                if (alreadyVisible.Contains(entry.Name)) continue;
                if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var type = assembly.GetTypeByMetadataName(entry.MetadataName);
                if (type == null) continue;

                // First sighting of this name -> tentatively importable; a second, different type
                // with the same name makes it ambiguous -> null marks it as excluded.
                byName[entry.Name] = byName.TryGetValue(entry.Name, out var existing) && existing != null
                    ? null
                    : type;
            }
        }

        foreach (var type in byName.Values)
            if (type != null)
                yield return type;
    }

    // Flattened index of every public importable type in a NuGet-reference assembly, built once per
    // reference and cached for the process lifetime (shared across every tab/service instance, same
    // sharing rationale as ReferenceCache — a MetadataReference is immutable, so its type index is
    // too). Walking the whole namespace tree (EnumerateNamespaceTypes) on every completion keystroke
    // made latency scale with the assembly's total public-type count; this makes each request a cheap
    // prefix scan over already-flattened plain strings instead. Entries store the metadata name
    // rather than the symbol itself, since INamedTypeSymbol instances aren't safe to reuse across
    // different Compilation instances — FindImportableTypes re-resolves only the (much smaller,
    // prefix-matched) subset via IAssemblySymbol.GetTypeByMetadataName against the current compilation.
    private static readonly ConcurrentDictionary<MetadataReference, IReadOnlyList<ImportableTypeEntry>>
        _importableTypeIndexCache = new();

    private readonly record struct ImportableTypeEntry(string Name, string MetadataName);

    private static IReadOnlyList<ImportableTypeEntry> GetImportableTypeIndex(
        IAssemblySymbol assembly, MetadataReference reference)
    {
        if (_importableTypeIndexCache.TryGetValue(reference, out var cached)) return cached;

        var entries = EnumerateNamespaceTypes(assembly.GlobalNamespace)
            .Select(t => new ImportableTypeEntry(t.Name, GetFullMetadataName(t)))
            .ToArray();
        return _importableTypeIndexCache.GetOrAdd(reference, entries);
    }

    private static string GetFullMetadataName(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        return ns is { IsGlobalNamespace: false } ? $"{ns.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamespaceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    foreach (var type in EnumerateNamespaceTypes(childNamespace))
                        yield return type;
                    break;
                case INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } type:
                    yield return type;
                    break;
            }
        }
    }

    // Walks back from position over identifier characters in the user's own code (not the
    // preamble-prefixed document Roslyn sees) — the same "what word is being typed" idea as
    // CodeTextEditor.GetWordStart, just server-side and string-only (no syntax tree needed for this).
    private static string GetIdentifierPrefix(string code, int position)
    {
        var end = Math.Clamp(position, 0, code.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
            start--;
        return code[start..end];
    }

    // Locals/parameters (the innermost scope) sort before members of the containing type, which sort
    // before other (in-scope) types, which sort before not-yet-imported types (rank 3, see
    // BuildImportableTypeItem — TODO.md item 60 point 6: "visible types → importable types"), which
    // sort before namespaces — so typing a few letters shows what's actually nearby before it shows
    // something merely imported/visible, and before something that isn't even imported yet
    // (TODO.md #51 follow-up: "the in-scope thing first, then a level up").
    private static int ScopeRank(ISymbol symbol) => symbol switch
    {
        ILocalSymbol or IParameterSymbol => 0,
        IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol => 1,
        INamedTypeSymbol => 2,
        INamespaceSymbol => 4,
        _ => 2,
    };

    // Whether a member found via LookupSymbols(container: type) actually belongs on the side of the
    // dot it was requested for — "DateTime." (static qualifier) vs "d." (instance qualifier),
    // ANALYSIS.md #52. Nested types are only ever reachable through the type itself (there's no such
    // thing as `instance.NestedType`), regardless of the nested type's own staticness, hence the
    // special case rather than just comparing symbol.IsStatic for everything.
    private static bool MatchesQualifierStaticness(ISymbol symbol, bool isStaticQualifier) => symbol switch
    {
        INamedTypeSymbol => isStaticQualifier,
        _ => symbol.IsStatic == isStaticQualifier,
    };

    // ICompletionItem.Priority — the editor's tie-break among items of otherwise-equal match quality
    // (see AvaloniaEdit.CodeCompletion.CompletionList.SelectItem: quality decides first, Priority only
    // breaks ties). Derived from the same ScopeRank used for SortText, rather than the UI layer's
    // former Kind == Method ? 1.0 : 0.9 hardcode (ANALYSIS.md #55) — so a local sitting right next to
    // an equally-good-matching method actually wins the tie-break its ScopeRank already implies.
    private static double ScopeRankPriority(int rank) => rank switch
    {
        0 => 1.0,  // locals/parameters
        1 => 0.8,  // members
        2 => 0.6,  // types
        3 => 0.5,  // importable (not-yet-imported) types
        _ => 0.4,  // namespaces
    };

    // ── Signature Help ─────────────────────────────────────────────────────────

    public Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code, int position, char? triggerCharacter = null, CancellationToken ct = default)
        => Task.Run(async () => (ISignatureHelpResult?)await TryGetSignatureHelpAsync(code, position, ct), ct);

    private async Task<ISignatureHelpResult?> TryGetSignatureHelpAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = position + HeaderLength(code);
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var node = root.FindNode(new TextSpan(Math.Max(0, pos - 1), 1), getInnermostNodeForTie: true);

            // "new Foo(" behaves the same as a method call for signature-help purposes — it just
            // wasn't recognized at all before (ANALYSIS.md #53), so opening a constructor's argument
            // list never showed its parameters. BaseObjectCreationExpressionSyntax is the shared base
            // of both `new Foo(...)` and the target-typed `new(...)`.
            var callLike = node.AncestorsAndSelf()
                .FirstOrDefault(n => n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax);
            var argumentList = callLike switch
            {
                InvocationExpressionSyntax inv => inv.ArgumentList,
                BaseObjectCreationExpressionSyntax oce => oce.ArgumentList,
                _ => null,
            };
            if (callLike == null || argumentList == null) return null;

            var argIndex = CountArgumentsBefore(argumentList, pos);
            var argCount = argumentList.Arguments.Count;
            var isArgumentSlotEmpty = IsArgumentSlotEmpty(argumentList, argIndex, pos);

            var symInfo = model.GetSymbolInfo(callLike);
            var methods = (symInfo.Symbol is IMethodSymbol s ? [s] : Array.Empty<IMethodSymbol>())
                .Concat(symInfo.CandidateSymbols.OfType<IMethodSymbol>())
                .ToList();

            // TODO.md #60.9 ("все перегрузки"): Roslyn only populates CandidateSymbols when
            // resolution is ambiguous — a call already typed correctly and unambiguously resolves
            // straight to one Symbol, which used to collapse "all overloads" down to just that one
            // even when the method has siblings (the insight window's up/down cycling had nothing
            // to cycle through for the single most common case: sitting inside an already-valid
            // call). Always try the full member/constructor group instead of only when methods is
            // still empty, falling back to whatever GetSymbolInfo alone found only if the group
            // lookup itself comes up empty (e.g. invoking through a delegate-typed local, which has
            // no "group" to speak of). SelectBestOverloadIndex below re-derives which entry actually
            // matches what's typed, so widening this list doesn't change what starts out highlighted.
            if (callLike is InvocationExpressionSyntax { Expression: { } target })
            {
                var group = model.GetMemberGroup(target).OfType<IMethodSymbol>().ToList();
                if (group.Count > 0) methods = group;
            }
            else if (callLike is BaseObjectCreationExpressionSyntax objectCreation)
            {
                // GetTypeInfo on the object-creation EXPRESSION itself, not its .Type child node —
                // the latter (what this used to read) comes back null for a type declared in the
                // same document being analyzed (reproduced by TryGetSignatureHelpAsync's own tests:
                // a locally-declared class's constructor overloads never showed up at all, only a
                // referenced-assembly type's like System.Text.StringBuilder's did) — apparently a
                // quirk of this in-memory AdhocWorkspace setup, not something specific to
                // target-typed `new(...)` (which has no .Type node to read in the first place, the
                // original reason this expression-based lookup existed at all, just not used
                // unconditionally).
                var createdType = model.GetTypeInfo(objectCreation).Type;
                if (createdType is INamedTypeSymbol namedType)
                {
                    var ctors = namedType.Constructors.Where(c => !c.IsImplicitlyDeclared).ToList();
                    if (ctors.Count > 0) methods = ctors;
                }
            }

            if (methods.Count == 0) return null;

            var items = methods.Select(m => (ISignatureHelpItem)new SimpleSignatureHelpItem(m, argIndex)).ToList();
            var selectedIndex = SelectBestOverloadIndex(methods, argumentList, argCount, model);

            return new SimpleSignatureHelpResult(items, selectedIndex, argIndex, isArgumentSlotEmpty);
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Signature help failed"); return null; }
    }

    // TODO.md #53 remainder: picking the highlighted overload by argument COUNT alone means e.g.
    // Foo(1, "x") highlights Foo(int, int) over Foo(int, string) whenever the int-int overload sorts
    // first — the arg count matches either way, but only one of them actually type-checks. This is a
    // lightweight approximation of real overload resolution, not a reimplementation of it: it prefers
    // the first candidate where every already-typed argument has an implicit (or identity) conversion
    // to the corresponding parameter, and falls back to the pre-existing count-only match — unchanged
    // — for anything that approximation can't confidently call (params/named/optional arguments,
    // nothing typed yet, or no candidate type-checks perfectly).
    private static int SelectBestOverloadIndex(
        List<IMethodSymbol> methods, ArgumentListSyntax argumentList, int argCount, SemanticModel model)
    {
        var typeAware = methods.FindIndex(m =>
        {
            if (m.Parameters.Length < argCount) return false;
            for (var i = 0; i < argCount; i++)
            {
                var argType = model.GetTypeInfo(argumentList.Arguments[i].Expression).ConvertedType;
                if (argType == null) continue; // couldn't infer (e.g. still typing) — don't penalize
                if (!model.Compilation.ClassifyConversion(argType, m.Parameters[i].Type).IsImplicit)
                    return false;
            }
            return true;
        });
        if (typeAware >= 0) return typeAware;

        var arityOnly = methods.FindIndex(m => m.Parameters.Length >= argCount);
        return arityOnly >= 0 ? arityOnly : methods.Count - 1;
    }

    // A position inside a // or /* */ comment (including /// doc comments) has nothing to complete —
    // model.LookupSymbols(pos)/GetSymbolInfo don't know about lexical trivia, so without this guard
    // they'd happily return whatever's in scope while the user is just typing prose.
    private static bool IsInsideComment(SyntaxNode root, int position)
    {
        // TextSpan.Contains is half-open ([start, end)), so a position sitting exactly at the end of
        // a comment's span — which is exactly where the caret is while still typing an unterminated
        // "//..."/"/*..." at the point nothing follows it yet — doesn't count as "inside" by the
        // first check. Falling back to position - 1 catches that (by far the most common) case.
        return IsCommentTrivia(root.FindTrivia(position))
            || (position > 0 && IsCommentTrivia(root.FindTrivia(position - 1)));
    }

    private static bool IsCommentTrivia(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.DocumentationCommentExteriorTrivia);

    // A position inside the text of a string/char literal has nothing to complete either — unlike
    // comments this isn't trivia, it's an ordinary token (StringLiteralToken etc.), so it needs its
    // own check rather than falling out of IsInsideComment (ANALYSIS.md #52). Interpolation holes
    // (the `{expr}` parts of `$"..."`) are deliberately NOT covered — their contents are ordinary
    // expression tokens (identifiers, dots, ...), not one of the kinds checked here, so completion
    // still works inside them, same as VS/Rider.
    private static bool IsInsideStringOrCharLiteral(SyntaxNode root, int position)
    {
        // Same half-open-span boundary concern as IsInsideComment: the caret sitting at the very end
        // of an in-progress, unterminated literal (by far the most common case while typing) is at
        // the token's end, so also check position - 1.
        return IsStringOrCharLiteralToken(root.FindToken(position))
            || (position > 0 && IsStringOrCharLiteralToken(root.FindToken(position - 1)));
    }

    private static bool IsStringOrCharLiteralToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.StringLiteralToken)
            || token.IsKind(SyntaxKind.CharacterLiteralToken)
            || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
            || token.IsKind(SyntaxKind.Utf8StringLiteralToken)
            || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken);

    // ANALYSIS.md TODO #68: the caret sitting inside the NAME of a fresh declaration (a method/class/
    // property/variable/parameter the user is currently naming, e.g. "void Mai|" or "private |") isn't
    // an expression position — LookupSymbols/GetSymbolInfo don't distinguish "referencing an existing
    // symbol" from "declaring a brand-new one", so without this guard the editor would happily offer
    // (and auto-commit on the very next keystroke) some unrelated in-scope symbol that merely shares a
    // prefix with the name being typed, clobbering it. Returning null here means IsCommitCharacter is
    // safe by construction — there's simply no completion window to close over the declared name.
    private static bool IsDeclarationNamePosition(SyntaxNode root, int position)
    {
        return IsDeclarationIdentifierToken(root.FindToken(position))
            || (position > 0 && IsDeclarationIdentifierToken(root.FindToken(position - 1)));
    }

    // Every syntax kind below carries its own declared name as a plain Identifier token (not a
    // qualified/dotted NameSyntax, unlike e.g. namespace declarations — deliberately not covered here,
    // out of scope for #68's reported symptom) — checking `== token` (not just "is this kind of node")
    // rules out completion inside some OTHER part of the same declaration (a method's return type, a
    // parameter's type, an attribute, ...), where the caret is legitimately in expression/type position.
    private static bool IsDeclarationIdentifierToken(SyntaxToken token) => token.Parent switch
    {
        MethodDeclarationSyntax n => n.Identifier == token,
        LocalFunctionStatementSyntax n => n.Identifier == token,
        ClassDeclarationSyntax n => n.Identifier == token,
        StructDeclarationSyntax n => n.Identifier == token,
        InterfaceDeclarationSyntax n => n.Identifier == token,
        RecordDeclarationSyntax n => n.Identifier == token,
        EnumDeclarationSyntax n => n.Identifier == token,
        EnumMemberDeclarationSyntax n => n.Identifier == token,
        PropertyDeclarationSyntax n => n.Identifier == token,
        VariableDeclaratorSyntax n => n.Identifier == token,
        ParameterSyntax n => n.Identifier == token,
        TypeParameterSyntax n => n.Identifier == token,
        _ => false,
    };

    // See _queryContinuationKeywords — true when the caret sits anywhere inside a LINQ query
    // expression (from ... to the final select/group-by clause) or a query continuation
    // (`into` ... onward), the only place from/join/into/let/orderby/select/group/on/equals/
    // ascending/descending/by mean anything.
    private static bool IsInsideQueryExpression(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is QueryExpressionSyntax or QueryContinuationSyntax);

    // See _accessorKeywords — true when the caret sits inside a property/indexer/event's accessor
    // list (covers the empty "{ }" case, where the closing brace token itself belongs to the
    // AccessorListSyntax) or inside an already-parsed accessor's own body.
    private static bool IsInsideAccessorContext(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is AccessorListSyntax or AccessorDeclarationSyntax);

    // Same half-open-span boundary concern as IsInsideComment/IsInsideStringOrCharLiteral above: the
    // caret while still typing sits at a token's end (exclusive), so also check position - 1.
    private static bool HasAncestorMatching(SyntaxNode root, int position, Func<SyntaxNode, bool> predicate)
    {
        return Check(position) || (position > 0 && Check(position - 1));

        bool Check(int pos) => root.FindToken(pos).Parent?.AncestorsAndSelf().Any(predicate) == true;
    }

    private static int CountArgumentsBefore(ArgumentListSyntax argList, int position)
    {
        var count = 0;
        foreach (var comma in argList.Arguments.GetSeparators())
        {
            if (comma.SpanStart < position) count++;
            else break;
        }
        return count;
    }

    // TODO.md #65: no argument has been parsed at argIndex at all (right after `(`, or right after a
    // trailing `,` with nothing typed since — Roslyn's error recovery doesn't produce an Arguments
    // entry for either), or one has but position sits at or before its own start (caret placed back
    // at the very start of an already-typed argument, e.g. via Home/click) — either way, nothing has
    // been typed for this slot yet.
    private static bool IsArgumentSlotEmpty(ArgumentListSyntax argList, int argIndex, int position)
        => argIndex >= argList.Arguments.Count || position <= argList.Arguments[argIndex].SpanStart;

    // ── Quick Info ─────────────────────────────────────────────────────────────

    public Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code, int position, CancellationToken ct = default)
        => Task.Run(async () => (IQuickInfoResult?)await TryGetQuickInfoAsync(code, position, ct), ct);

    private async Task<IQuickInfoResult?> TryGetQuickInfoAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = position + HeaderLength(code);
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (token == default) return null;

            var symInfo = model.GetSymbolInfo(token.Parent!);
            var symbol = symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault()
                ?? (ISymbol?)model.GetTypeInfo(token.Parent!).Type;

            if (symbol == null) return null;

            return new SimpleQuickInfoResult(
                symbol.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(symbol));
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Quick info failed"); return null; }
    }

    // ── Document Highlights ──────────────────────────────────────────────────────

    // TODO.md #62 "подсветка вхождений": Roslyn ships a ready-made IDocumentHighlightsService for
    // exactly this, but it (and its result types DocumentHighlights/HighlightSpan) are internal to
    // Microsoft.CodeAnalysis.Features — not reachable from here even though that assembly is already
    // loaded into DotPadRoslynHost's MEF composition (confirmed directly: referencing the type by
    // name fails to compile with CS0122, "inaccessible due to its protection level"). Reimplemented
    // with only public Roslyn API instead: find the symbol under the caret, then scan every
    // identifier token in the document for one that resolves to the same symbol.
    public Task<IReadOnlyList<IDocumentHighlightSpan>?> GetDocumentHighlightsAsync(
        string code, int position, CancellationToken ct = default)
        => Task.Run(async () => (IReadOnlyList<IDocumentHighlightSpan>?)await TryGetDocumentHighlightsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<IDocumentHighlightSpan>?> TryGetDocumentHighlightsAsync(
        string code, int position, CancellationToken ct)
    {
        try
        {
            var found = await FindSymbolAndOccurrencesAsync(code, position, ct);
            return found?.Spans;
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Document highlights failed"); return null; }
    }

    // ── Rename ────────────────────────────────────────────────────────────────────

    public Task<IRenameInfo?> GetRenameInfoAsync(string code, int position, CancellationToken ct = default)
        => Task.Run(async () => (IRenameInfo?)await TryGetRenameInfoAsync(code, position, ct), ct);

    private async Task<IRenameInfo?> TryGetRenameInfoAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var found = await FindSymbolAndOccurrencesAsync(code, position, ct);
            if (found is not { } result) return null;

            // Only symbols the user's own script actually declares can be renamed — a metadata/
            // framework symbol (e.g. "WriteLine" in Console.WriteLine) has no source location to
            // rename AT; renaming its call sites in the script without renaming the framework
            // method itself would just turn correct code into a compile error.
            if (!result.Symbol.Locations.Any(l => l.IsInSource)) return null;

            return new SimpleRenameInfo(result.Symbol.Name, result.Spans);
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Rename info failed"); return null; }
    }

    // ── Go to Definition (F12) ───────────────────────────────────────────────────

    public Task<IDefinitionResult?> GetDefinitionAsync(string code, int position, CancellationToken ct = default)
        => Task.Run(async () => (IDefinitionResult?)await TryGetDefinitionAsync(code, position, ct), ct);

    private async Task<IDefinitionResult?> TryGetDefinitionAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var header = HeaderLength(code);
            var pos = position + header;
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

            var symbol = ResolveTokenSymbol(model, token);
            if (symbol == null) return null;

            if (symbol.Locations.FirstOrDefault(l => l.IsInSource) is { } sourceLocation)
            {
                var offset = sourceLocation.SourceSpan.Start - header;
                if (offset < 0) return null; // declared inside the prepended preamble, not user code
                return new SimpleDefinitionResult(
                    IsInSource: true, Offset: offset,
                    AssemblyPath: null, ContainingTypeFullName: null, MemberName: null,
                    ParameterTypeNames: [], DisplayName: symbol.Name);
            }

            // Extension methods resolve to their REDUCED form (receiver dropped from Parameters) —
            // metadata (and so the decompiler's own type system) only knows the original static
            // method, receiver included as parameter 0. Un-reduce before reading Name/Parameters so
            // the parameter list CSharpDecompilerService is asked to match against reflects reality.
            if (symbol is IMethodSymbol { ReducedFrom: { } original })
                symbol = original;

            var containingType = symbol is INamedTypeSymbol selfType ? selfType : symbol.ContainingType;
            if (containingType is null) return null; // e.g. a namespace — nothing to decompile

            var reference = model.Compilation.GetMetadataReference(containingType.ContainingAssembly);
            if (reference is not PortableExecutableReference { FilePath: { } assemblyPath }) return null;

            var isTypeItself = SymbolEqualityComparer.Default.Equals(symbol, containingType);
            var parameterTypeNames = symbol is IMethodSymbol { Parameters: var parameters }
                ? parameters.Select(p => p.Type.Name).ToArray()
                : [];

            return new SimpleDefinitionResult(
                IsInSource: false, Offset: 0,
                AssemblyPath: assemblyPath,
                ContainingTypeFullName: GetReflectionTypeName(containingType.OriginalDefinition),
                MemberName: isTypeItself ? null : symbol.Name,
                ParameterTypeNames: parameterTypeNames,
                DisplayName: symbol.ToDisplayString());
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Go to definition failed"); return null; }
    }

    // Reflection-style name (Namespace.Type`Arity+Nested`Arity) — what ICSharpCode.Decompiler's
    // FullTypeName(string) constructor expects. INamedTypeSymbol.MetadataName already carries the
    // `Arity suffix for open generics; OriginalDefinition (applied by the caller) strips any
    // constructed-generic type arguments first (e.g. List<int> -> List<T>) since a FullTypeName names
    // the open generic definition, not one particular instantiation.
    private static string GetReflectionTypeName(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } outer)
            return GetReflectionTypeName(outer.OriginalDefinition) + "+" + type.MetadataName;
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + type.MetadataName
            : type.MetadataName;
    }

    // ── Go to Implementation (Ctrl+F12) ──────────────────────────────────────────

    public Task<IReadOnlyList<IImplementationLocation>?> GetImplementationsAsync(
        string code, int position, CancellationToken ct = default)
        => Task.Run(async () => (IReadOnlyList<IImplementationLocation>?)await TryGetImplementationsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<IImplementationLocation>?> TryGetImplementationsAsync(
        string code, int position, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var header = HeaderLength(code);
            var pos = position + header;
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

            var symbol = ResolveTokenSymbol(model, token);
            if (symbol == null) return null;

            var solution = document.Project.Solution;

            // FindImplementationsAsync only covers interface members/types — an abstract or virtual
            // member (not on an interface) needs FindOverridesAsync instead. A single-file script's
            // own AdhocWorkspace only ever contains the script itself, so either call can only ever
            // surface OTHER declarations inside that same document — never metadata.
            IEnumerable<ISymbol> found = symbol is INamedTypeSymbol namedType
                ? await SymbolFinder.FindImplementationsAsync(namedType, solution, cancellationToken: ct)
                : await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct);
            var implementations = found.ToList();

            if (implementations.Count == 0 && IsOverridable(symbol))
                implementations = (await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct)).ToList();

            if (implementations.Count == 0) return null;

            var results = new List<IImplementationLocation>();
            foreach (var impl in implementations)
            {
                if (impl.Locations.FirstOrDefault(l => l.IsInSource) is not { } location) continue;
                var offset = location.SourceSpan.Start - header;
                if (offset < 0) continue; // inside the prepended usings preamble, not user code
                results.Add(new SimpleImplementationLocation(offset, impl.ToDisplayString()));
            }

            return results.Count == 0 ? null : results;
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Go to implementation failed"); return null; }
    }

    private static bool IsOverridable(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.IsAbstract || m.IsVirtual,
        IPropertySymbol p => p.IsAbstract || p.IsVirtual,
        IEventSymbol e => e.IsAbstract || e.IsVirtual,
        _ => false,
    };

    // Shared by GetDocumentHighlightsAsync and GetRenameInfoAsync — both are "find the symbol under
    // the caret, then every identifier token elsewhere in the document that resolves to that same
    // symbol"; they only differ in what they do with the result afterward (highlight vs. offer to
    // rename, the latter additionally filtering to source-declared symbols only).
    private async Task<(ISymbol Symbol, List<IDocumentHighlightSpan> Spans)?> FindSymbolAndOccurrencesAsync(
        string code, int position, CancellationToken ct)
    {
        var document = SyncDocument(code);
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null) return null;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return null;

        var pos = position + HeaderLength(code);
        if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

        var token = root.FindToken(pos);
        if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

        var symbol = ResolveTokenSymbol(model, token);
        if (symbol == null) return null;

        var header = HeaderLength(code);
        var spans = new List<IDocumentHighlightSpan>();
        foreach (var candidate in root.DescendantTokens())
        {
            ct.ThrowIfCancellationRequested();
            // Cheap text check before the semantic lookup — most identifiers in the document
            // aren't even textually the same name, no need to ask the semantic model about them.
            if (!candidate.IsKind(SyntaxKind.IdentifierToken) || candidate.ValueText != token.ValueText)
                continue;

            var candidateSymbol = ResolveTokenSymbol(model, candidate);
            if (candidateSymbol == null || !SymbolEqualityComparer.Default.Equals(candidateSymbol, symbol))
                continue;

            var userStart = candidate.SpanStart - header;
            if (userStart < 0) continue; // inside the prepended usings preamble, not user code
            spans.Add(new SimpleDocumentHighlightSpan(userStart, candidate.Span.Length));
        }

        return (symbol, spans);
    }

    // Shared by both the caret's own token and every candidate token scanned against it — a
    // declaration site (e.g. the identifier in "var x = ...") has no GetSymbolInfo result of its
    // own (that call is for usages/references), only GetDeclaredSymbol; a usage is the opposite.
    // Trying both, in this order, covers either kind of identifier token uniformly.
    private static ISymbol? ResolveTokenSymbol(SemanticModel model, SyntaxToken token) =>
        model.GetSymbolInfo(token.Parent!).Symbol ?? model.GetDeclaredSymbol(token.Parent!);

    // ── Format ─────────────────────────────────────────────────────────────────

    // Formats in a throwaway workspace/project containing only the user's code (no preamble) so
    // Roslyn's Formatter output maps back to the editor text directly, with no preamble/code
    // boundary whitespace to reconcile.
    public async Task<string?> FormatDocumentAsync(string code, CancellationToken ct = default)
    {
        try
        {
            using var formatWorkspace = new AdhocWorkspace(DotPadRoslynHost.HostServices);
            var projectId = ProjectId.CreateNewId("Format");
            formatWorkspace.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: "Format",
                assemblyName: "Format",
                language: LanguageNames.CSharp,
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest)));

            var document = formatWorkspace.AddDocument(projectId, "Script.cs", SourceText.From(code, Encoding.UTF8));
            var formatted = await Formatter.FormatAsync(document, cancellationToken: ct);
            var text = await formatted.GetTextAsync(ct);
            return text.ToString();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Format failed"); return null; }
    }

    // ── Refactorings (CodeRefactoringProvider hosting in the lightbulb, TODO.md #74) ─────────────

    public Task<IReadOnlyList<ICodeFixInfo>?> GetRefactoringsAsync(
        string code, int position, CancellationToken ct = default)
        => Task.Run(async () => (IReadOnlyList<ICodeFixInfo>?)await TryGetRefactoringsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<ICodeFixInfo>?> TryGetRefactoringsAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            if (position < 0 || position > code.Length) return null;

            var document = SyncDocument(code);
            var headerLength = HeaderLength(code);
            var span = new TextSpan(position + headerLength, 0);

            // 1-based, same convention as CodeFixEntry.Line/RoslynCodeFixEngine's own diagnostic-fix
            // line lookup — LinePosition.Line itself is 0-based.
            var userLine = SourceText.From(code).Lines.GetLinePosition(position).Line + 1;

            return await _codeFixEngine.ComputeRefactoringsAsync(document, span, code, headerLength, userLine, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRefactoringsAsync failed"); return null; }
    }

    // TODO.md #74 "оптимизация IntelliSenseService" (сто четвёртый проход) — the deferred half of
    // both CodeFixesChanged's fixes and GetRefactoringsAsync's refactorings, see IIntelliSenseService.
    // MaterializeFixAsync's own doc comment. No SyncDocument call here — the cached CodeAction
    // RoslynCodeFixEngine looks key up already carries its own document snapshot internally (captured
    // when it was registered), so materializing it needs nothing from the live workspace at all, only
    // the code/preamble metrics needed for the relocating-preamble math.
    public Task<IMaterializedFix?> MaterializeFixAsync(string code, string key, CancellationToken ct = default)
        => Task.Run(async () => (IMaterializedFix?)await TryMaterializeFixAsync(code, key, ct), ct);

    private async Task<IMaterializedFix?> TryMaterializeFixAsync(string code, string key, CancellationToken ct)
    {
        try
        {
            var headerLength = HeaderLength(code);
            var materialized = await _codeFixEngine.MaterializeAsync(key, code, headerLength, ct);
            return materialized is null
                ? null
                : new SimpleMaterializedFix(materialized.Value.NewCode, materialized.Value.UsingsToAdd);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "MaterializeFixAsync failed"); return null; }
    }

    // ── Background analysis (diagnostics + semantic tokens) ───────────────────

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;
    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;
    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;
    public event Action<string, IReadOnlyList<IInlayHint>>? InlayHintsChanged;

    public void NotifyTextChanged(string code)
    {
        // Not disposed here — a superseded background analysis pass (Task.Run below) can still be
        // mid-flight past this point (e.g. inside RoslynCodeFixEngine, which doesn't poll the token
        // between individual CodeFixProviders — see Dispose's own comment), and registering/reading
        // against an already-disposed CancellationTokenSource throws ObjectDisposedException
        // (ANALYSIS.md #89). A CancellationTokenSource whose WaitHandle is never touched holds no
        // unmanaged resources, so leaving the superseded one for the GC is safe; Dispose() below
        // (this instance's own teardown, after _backgroundAnalysisTask is awaited/timed out) still
        // disposes the final one.
        _diagnosticsCts?.Cancel();
        _diagnosticsCts = new CancellationTokenSource();
        var ct = _diagnosticsCts.Token;

        _backgroundAnalysisTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_backgroundAnalysisDebounce, ct);
                var document = SyncDocument(code);
                await Task.WhenAll(
                    ComputeDiagnosticsAndCodeFixesAsync(code, document, ct),
                    ComputeSemanticTokensAsync(code, document, ct),
                    ComputeInlayHintsAsync(code, document, ct));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Background analysis failed"); }
        }, CancellationToken.None);
    }

    private async Task ComputeSemanticTokensAsync(string code, Document document, CancellationToken ct)
    {
        var headerLength = HeaderLength(code);
        var textLen = headerLength + code.Length;
        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, textLen), ct);

        var result = new List<ISemanticToken>();
        foreach (var span in spans)
        {
            var kind = ClassifySpan(span.ClassificationType);
            if (kind == SemanticTokenKind.None) continue;

            var start = span.TextSpan.Start - headerLength;
            if (start < 0) continue;
            var end = Math.Min(span.TextSpan.End - headerLength, code.Length);
            if (start >= end) continue;

            result.Add(new SimpleSemanticToken(start, end - start, kind));
        }

        SemanticTokensChanged?.Invoke(code, result);
    }

    // TODO.md #62 "inlay hints" — inferred `var x` / `foreach (var x in ...)` type annotations only
    // (Roslyn's own CSharpInlineTypeHintsService also covers lambda parameters, implicit `new()`, and
    // collection expressions — internal API, decompiled to confirm exact placement/format below, not
    // ported wholesale; those three are lower-value, left for a follow-up if wanted). Single-variable
    // declarations only (`var a = 1, b = 2;` skipped entirely) — matches Roslyn's own behavior; a
    // multi-declarator statement can only have one inferred type anyway, so hinting just the first
    // variable would misleadingly suggest it doesn't apply to the rest.
    private async Task ComputeInlayHintsAsync(string code, Document document, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (model is null || root is null)
        {
            InlayHintsChanged?.Invoke(code, []);
            return;
        }

        var headerLength = HeaderLength(code);
        var result = new List<IInlayHint>();

        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            if (declaration.Type is not { IsVar: true } typeSyntax) continue;
            if (declaration.Variables.Count != 1) continue;
            var identifier = declaration.Variables[0].Identifier;
            if (identifier.IsMissing) continue;

            var type = model.GetTypeInfo(typeSyntax, ct).Type;
            AddHint(result, type, identifier.SpanStart, typeSyntax.SpanStart, headerLength, code, model);
        }

        foreach (var forEach in root.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (forEach.Type is not { IsVar: true } typeSyntax) continue;
            if (forEach.Identifier.IsMissing) continue;

            var type = model.GetForEachStatementInfo(forEach).ElementType;
            AddHint(result, type, forEach.Identifier.SpanStart, typeSyntax.SpanStart, headerLength, code, model);
        }

        InlayHintsChanged?.Invoke(code, result);

        // Roslyn's own CSharpInlineTypeHintsService (IsValidType) only excludes null and a type
        // literally named "var" (e.g. a user-defined `class var` — legal, if unusual, C#); it doesn't
        // special-case error types beyond that, and neither does this.
        static void AddHint(List<IInlayHint> hints, ITypeSymbol? type, int identifierPos, int typeSyntaxPos,
            int headerLength, string code, SemanticModel model)
        {
            if (type is null || type.Name == "var") return;

            var offset = identifierPos - headerLength;
            if (offset < 0 || offset > code.Length) return;

            var display = type.ToMinimalDisplayString(model, typeSyntaxPos, _inlayHintTypeFormat);
            hints.Add(new SimpleInlayHint(offset, display + " "));
        }
    }

    // ── Diagnostics + code fixes (real Roslyn CodeFixProviders — see RoslynCodeFixEngine) ──────

    // Combined into one pass — these used to be two separate methods, each independently calling
    // model.GetDiagnostics(...), which forces Roslyn to re-run the full diagnostic/analyzer engine
    // over the document; GetSemanticModelAsync itself is cached per document version, but
    // GetDiagnostics is not, so calling it twice on the same model did the same binding work twice
    // on every 500ms background tick (ANALYSIS.md TODO #56).
    private async Task ComputeDiagnosticsAndCodeFixesAsync(string code, Document document, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null)
        {
            DiagnosticsChanged?.Invoke(code, []);
            CodeFixesChanged?.Invoke(code, []);
            return;
        }

        // TODO.md #74 "Пайплайн анализаторов" — Roslyn's own IDE-style analyzers ("use var", "make
        // readonly", ...) alongside the compiler's own diagnostics. GetCompilationAsync is the same
        // cached compilation model.GetDiagnostics() already reads from (Roslyn caches per Project
        // version), not a second build.
        var compilation = await document.Project.GetCompilationAsync(ct);
        var analyzerDiagnostics = compilation is null
            ? []
            : await _analyzerEngine.ComputeAsync(compilation, ct);

        var headerLength = HeaderLength(code);
        var visible = new List<IDiagnosticInfo>();
        var forCodeFixes = new List<Diagnostic>();
        foreach (var d in model.GetDiagnostics(cancellationToken: ct).Concat(analyzerDiagnostics))
        {
            // Unlike compiler diagnostics (never actually Hidden in practice), most of the analyzer
            // diagnostics above fire at Hidden severity BY DESIGN — no squiggle, only a CodeFixProvider
            // keyed to that same diagnostic ID becoming available (RoslynAnalyzerEngine's own doc
            // comment). Still added to forCodeFixes either way — only excluded from the visible list.
            forCodeFixes.Add(d);
            if (d.Severity == RoslynSeverity.Hidden) continue;

            if (!d.Location.IsInSource) continue;

            var span = d.Location.SourceSpan;
            var start = span.Start - headerLength;
            if (start < 0) continue;

            // Clamp to code.Length before the start>=end skip below, same pattern as
            // ComputeSemanticTokensAsync/ComputeInlayHintsAsync's own bounds checks — needed since
            // TODO.md #107: RpadKind.Program's mirrored KindSuffix (the wrapper class's closing brace)
            // now follows the user's own code in the synced document, so a diagnostic anchored there
            // (or straddling into it) needs to not leak an out-of-range span to the editor.
            var end = Math.Min(Math.Max(span.End - headerLength, start + 1), code.Length);
            if (start >= end) continue;

            var severity = d.Severity switch
            {
                RoslynSeverity.Error => ContractSeverity.Error,
                RoslynSeverity.Warning => ContractSeverity.Warning,
                _ => ContractSeverity.Info,
            };
            visible.Add(new SimpleDiagnostic(start, end, d.GetMessage(), severity));
        }

        DiagnosticsChanged?.Invoke(code, visible);

        var fixes = await _codeFixEngine.ComputeFixesAsync(document, forCodeFixes, code, headerLength, ct);
        CodeFixesChanged?.Invoke(code, fixes);
    }

    private static SemanticTokenKind ClassifySpan(string type) => type switch
    {
        ClassificationTypeNames.ClassName or "record class name" => SemanticTokenKind.ClassName,
        ClassificationTypeNames.StructName or "record struct name" => SemanticTokenKind.StructName,
        ClassificationTypeNames.InterfaceName => SemanticTokenKind.InterfaceName,
        ClassificationTypeNames.EnumName => SemanticTokenKind.EnumName,
        ClassificationTypeNames.DelegateName => SemanticTokenKind.DelegateName,
        ClassificationTypeNames.TypeParameterName => SemanticTokenKind.TypeParameterName,
        ClassificationTypeNames.MethodName => SemanticTokenKind.MethodName,
        ClassificationTypeNames.ExtensionMethodName => SemanticTokenKind.ExtensionMethodName,
        ClassificationTypeNames.PropertyName => SemanticTokenKind.PropertyName,
        ClassificationTypeNames.FieldName or "constant name" => SemanticTokenKind.FieldName,
        ClassificationTypeNames.EventName => SemanticTokenKind.EventName,
        ClassificationTypeNames.NamespaceName => SemanticTokenKind.NamespaceName,
        ClassificationTypeNames.LocalName => SemanticTokenKind.LocalName,
        ClassificationTypeNames.ParameterName => SemanticTokenKind.ParameterName,
        _ => SemanticTokenKind.None,
    };

    // ── Package references ────────────────────────────────────────────────────

    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages)
    {
        var newPaths = packages
            .SelectMany(kv => FindPackageDlls(_nugetRoot, kv.Key, kv.Value.Version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newRefs = newPaths
            .Select(p => (MetadataReference)CreateReference(p))
            .ToList();

        var project = _workspace.CurrentSolution.GetProject(_documentId.ProjectId)!;

        // Keep base BCL refs, replace NuGet refs with the new set.
        var baseRefs = project.MetadataReferences
            .Where(r => !_nugetRefs.Contains(r))
            .ToList();

        _nugetRefs.Clear();
        _nugetRefs.AddRange(newRefs);

        _workspace.TryApplyChanges(
            project.WithMetadataReferences(baseRefs.Concat(newRefs).ToList()).Solution);
        // force re-sync on next analysis pass
        lock (_syncLock) { _lastCode = ""; }
    }

    private static IEnumerable<string> FindPackageDlls(string nugetRoot, string id, string version)
    {
        var libDir = Path.Combine(nugetRoot, id.ToLowerInvariant(), version, "lib");
        if (!Directory.Exists(libDir)) yield break;

        var available = Directory.EnumerateDirectories(libDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tfm in _tfmPreference)
        {
            if (!available.Contains(tfm)) continue;
            foreach (var dll in Directory.GetFiles(Path.Combine(libDir, tfm), "*.dll"))
                yield return dll;
            yield break;
        }
    }

    // Cancel() alone only *requests* cooperative cancellation — NotifyTextChanged's Task.Run body can
    // still be mid-flight (e.g. deep inside RoslynCodeFixEngine.ComputeFixesAsync, which doesn't poll
    // the token between individual CodeFixProviders) when this method would otherwise return
    // immediately, racing _workspace.Dispose() right below and continuing to burn CPU on a ThreadPool
    // thread after this service is considered "gone" by its caller. Observed as a same-process,
    // cross-test CPU-starvation flake in CI (background analysis from an earlier, already-disposed
    // test's service still running while a later test's own NotifyTextChanged competes for ThreadPool
    // capacity) — worse on Windows CI's weaker/more contended runners, but a real bug regardless of
    // platform: any caller that disposes right after only DiagnosticsChanged fires (which happens
    // *before* the codefix half of the same background pass, see ComputeDiagnosticsAndCodeFixesAsync)
    // was already at risk of this. Bounded, not unbounded — a pathological provider that truly never
    // checks its token shouldn't be able to hang Dispose (or, in production, a tab close) forever.
    public void Dispose()
    {
        _diagnosticsCts?.Cancel();
        try { _backgroundAnalysisTask?.Wait(_backgroundAnalysisDisposeTimeout); }
        catch { /* already logged from inside the task itself */ }
        _diagnosticsCts?.Dispose();
        _workspace.Dispose();
    }

    // ── Private model types ────────────────────────────────────────────────────

    private sealed record SimpleCompletionItem(
        string DisplayText, CompletionItemKind Kind, IReadOnlyList<DisplayTextSegment> Signature,
        string? Summary, string SortText, double Priority, IReadOnlyList<string> UsingsToAdd,
        string? Detail = null, int OverloadCount = 1, bool IsOwnMember = false,
        string? InsertTemplate = null) : ICompletionItem;

    private sealed class SimpleCompletionList(IReadOnlyList<ICompletionItem> items) : ICompletionList
    {
        public IReadOnlyList<ICompletionItem> Items => items;
    }

    private sealed class SimpleSignatureHelpResult(
        IReadOnlyList<ISignatureHelpItem> items, int selectedIndex, int argumentIndex, bool isArgumentSlotEmpty)
        : ISignatureHelpResult
    {
        public IReadOnlyList<ISignatureHelpItem> Items => items;
        public int SelectedItemIndex => selectedIndex;
        public int ArgumentIndex => argumentIndex;
        public bool IsArgumentSlotEmpty => isArgumentSlotEmpty;
    }

    private sealed class SimpleSignatureHelpItem : ISignatureHelpItem
    {
        public SimpleSignatureHelpItem(IMethodSymbol method, int argumentIndex)
        {
            Signature = method.ToDisplayParts(_briefFormat).ToSegments(EmphasizedParameter(method, argumentIndex));
            Summary = XmlDocSummary.GetSummary(method);
            Parameters = method.Parameters
                .Select(p => (ISignatureHelpParameter)new SimpleSignatureHelpParameter(method, p))
                .ToList();
        }

        public IReadOnlyList<DisplayTextSegment> Signature { get; }
        public string? Summary { get; }
        public IReadOnlyList<ISignatureHelpParameter> Parameters { get; }

        // The parameter the caret is currently sitting in, so the UI can bold it in the header the
        // way LINQPad/VS Code do — a `params` tail parameter stays emphasized past its own index.
        private static IParameterSymbol? EmphasizedParameter(IMethodSymbol method, int argumentIndex)
        {
            if (method.Parameters.Length == 0) return null;
            if (argumentIndex < method.Parameters.Length) return method.Parameters[argumentIndex];
            var last = method.Parameters[^1];
            return last.IsParams ? last : null;
        }
    }

    private sealed class SimpleSignatureHelpParameter : ISignatureHelpParameter
    {
        public SimpleSignatureHelpParameter(IMethodSymbol method, IParameterSymbol parameter)
        {
            Name = parameter.Name;
            Signature = parameter.ToDisplayParts(_briefFormat).ToSegments();
            Summary = XmlDocSummary.GetParameterSummary(method, parameter.Name);
        }

        public string Name { get; }
        public IReadOnlyList<DisplayTextSegment> Signature { get; }
        public string? Summary { get; }
    }

    private sealed record SimpleQuickInfoResult(
        IReadOnlyList<DisplayTextSegment> Signature, string? Summary) : IQuickInfoResult;

    private sealed record SimpleDocumentHighlightSpan(int Start, int Length) : IDocumentHighlightSpan;

    private sealed record SimpleRenameInfo(
        string CurrentName, IReadOnlyList<IDocumentHighlightSpan> Spans) : IRenameInfo;

    private sealed record SimpleDefinitionResult(
        bool IsInSource, int Offset, string? AssemblyPath, string? ContainingTypeFullName,
        string? MemberName, IReadOnlyList<string> ParameterTypeNames, string DisplayName) : IDefinitionResult;

    private sealed record SimpleImplementationLocation(int Offset, string DisplayName) : IImplementationLocation;

    private sealed record SimpleDiagnostic(
        int StartOffset, int EndOffset, string Message,
        ContractSeverity Severity) : IDiagnosticInfo;

    private sealed record SimpleSemanticToken(
        int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;

    private sealed record SimpleInlayHint(int Offset, string Text) : IInlayHint;

    private sealed record SimpleMaterializedFix(string NewCode, IReadOnlyList<string> UsingsToAdd) : IMaterializedFix;

}
