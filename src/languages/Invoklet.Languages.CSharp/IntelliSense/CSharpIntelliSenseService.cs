using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using ContractSeverity = Invoklet.Contracts.IntelliSense.DiagnosticSeverity;
using ExecutionRequest = Invoklet.Contracts.Models.ExecutionRequest;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Invoklet.Languages.CSharp.IntelliSense;

public sealed partial class CSharpIntelliSenseService : IIntelliSenseService
{


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
        // that happens to fuzzy-match the same letters doesn't
        // lose a tie-break to a keyword the user isn't actually reaching for. Priority matches —
        // lowest of all kinds, see ScopeRankPriority.
        .Select(kw => (ICompletionItem)new SimpleCompletionItem(
            kw, CompletionItemKind.Keyword, [new DisplayTextSegment(kw, SemanticTokenKind.Keyword)],
            Summary: null, SortText: $"9{kw}", Priority: 0.1, UsingsToAdd: [], Source: CompletionSourceKind.Keyword))
        .ToArray();

    // Contextual keywords with no legal meaning outside a specific syntax
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

    // The only keywords that are ever legal as a generic type argument are the predefined
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
            return SourcePositions.IsInsideQueryExpression(root, position);

        if (keyword.DisplayText == "value")
            return SourcePositions.HasAncestorMatching(root, position, n =>
                n is AccessorDeclarationSyntax a && a.Kind() is SyntaxKind.SetAccessorDeclaration
                    or SyntaxKind.AddAccessorDeclaration or SyntaxKind.RemoveAccessorDeclaration);

        if (_accessorKeywords.Contains(keyword.DisplayText))
            return SourcePositions.IsInsideAccessorContext(root, position);

        return true;
    }

    // True anywhere inside a generic type argument list (`List<`, `List<Str`,
    // `Dictionary<string, `, ...) — the only place where completion should be restricted to type-ish
    // symbols instead of every in-scope symbol. Reuses the same HasAncestorMatching position-1
    // fallback as every other "what syntax is the caret inside" check in this file (see
    // IsInsideQueryExpression/IsInsideAccessorContext above) since the caret while typing sits at a
    // token's end, not inside it.
    private static bool IsInsideTypeArgumentList(SyntaxNode root, int position) =>
        SourcePositions.HasAncestorMatching(root, position, n => n is TypeArgumentListSyntax);

    // Debounce for NotifyTextChanged's background diagnostics/semantic-tokens pass — long enough
    // that fast typing doesn't re-run the full Roslyn analysis on every keystroke, short enough to
    // still feel live. Settable only by tests (InternalsVisibleTo): a test that calls
    // NotifyTextChanged exactly once has nothing to debounce, and 500ms x ~500 such tests was most of
    // Keep the shared reference set stable across analysis requests.
    internal TimeSpan BackgroundAnalysisDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    // How long Dispose waits for an in-flight background analysis pass to actually observe
    // cancellation before giving up — see Dispose's own comment for why this is bounded rather than
    // unbounded.
    private static readonly TimeSpan _backgroundAnalysisDisposeTimeout = TimeSpan.FromSeconds(5);

    // Debounce, supersede and stale re-run of the background analysis.
    private readonly AnalysisScheduler _scheduler;

    private readonly ILogger<CSharpIntelliSenseService> _logger;
    // The text of the analysis document and the offsets into it; holds no workspace.
    private readonly ScriptDocumentLayout _layout = new();
    // The workspace of the analysis, its document and the baseline the sync diffs against.
    private readonly ScriptDocumentSession _session;
    // What the project is compiled against besides the script's text.
    private readonly ScriptContext _context;
    private readonly SymbolNavigation _navigation;
    private readonly SignatureHelp _signatureHelp;
    private readonly BreakpointConditionAnalyzer _breakpointConditions;
    private readonly DocumentFormatter _formatter;

    // Test-only visibility (InternalsVisibleTo) — confirms the FormattingOptions.NewLine override
    // below actually takes effect on the workspace's live option set. Environment.NewLine can't be
    // forced to '\r\n' on a non-Windows dev/CI machine, so this is the closest local check for the
    // fix without a real Windows run.
    internal string FormattingNewLineOptionForTests
        => _session.Workspace.CurrentSolution.Options.GetOption(FormattingOptions.NewLine, LanguageNames.CSharp);
    private readonly RoslynCodeFixEngine _codeFixEngine;
    private readonly RoslynAnalyzerEngine _analyzerEngine;

    // Invoklet's own analyzers are experimental and off until the host turns them on.
    private volatile bool _InvokletAnalyzersEnabled;

    public void UpdateInvokletAnalyzers(bool enabled) => _InvokletAnalyzersEnabled = enabled;
    // Test-only observability (InternalsVisibleTo): confirms that the sync took the incremental WithChanges path (the lineage of the
    // SourceText kept across calls) and did not fall back to SourceText.From every time.
    internal SourceText? LastSyncedSourceTextForTests => _session.LastSyncedSourceText;

    // Test-only: called by a background analysis pass once it holds its document snapshot, before it
    // analyses it, so a test can change the context at exactly that point.
    internal Action? AnalysisSnapshotTakenForTests { get; set; }
    // Test-only: called by a completion once it holds its document snapshot, before it reads it.
    internal Action? CompletionSnapshotTakenForTests { get; set; }


    /// <param name="referencePaths">The framework's assemblies, and others outside their folder.</param>
    /// <param name="additionalReferencePaths">Assemblies kept whatever the script's target framework,
    /// such as Invoklet.Runtime.</param>
    public CSharpIntelliSenseService(IEnumerable<string> referencePaths, ILogger<CSharpIntelliSenseService> logger,
        IEnumerable<string>? additionalReferencePaths = null)
    {
        _logger = logger;
        _scheduler = new AnalysisScheduler(
            () => BackgroundAnalysisDebounce,
            work => RoslynMessageCulture.RunAsync(work, CancellationToken.None),
            ex => _logger.LogWarning(ex, "Background analysis failed"),
            _backgroundAnalysisDisposeTimeout);

        var references = ReferenceSet.Classify(referencePaths, additionalReferencePaths);
        _session = new ScriptDocumentSession(_layout, references.All.Select(p => (MetadataReference)ScriptContext.CreateReference(p)).ToList());
        _context = new ScriptContext(_session, _layout, references, logger);
        _navigation = new SymbolNavigation(SyncDocument, _layout, _context, logger);
        _signatureHelp = new SignatureHelp(SyncDocument, _layout, logger);
        _breakpointConditions = new BreakpointConditionAnalyzer(SyncDocument, _layout);
        _formatter = new DocumentFormatter(_session, _layout, logger);

        _codeFixEngine = new RoslynCodeFixEngine(InvokletRoslynHost.CompositionHost, logger);
        _analyzerEngine = new RoslynAnalyzerEngine(logger);
    }

    public void UpdateGlobalUsings(IReadOnlyList<string> usings)
    {
        if (!_layout.SetExtraUsings(usings)) return;
        // force re-sync with the new preamble on the next analysis pass — also drop the cached
        // _lastSourceText/_lastFullText baseline (see their own doc comment): the preamble just
        // changed underneath it, so diffing against it would compute a TextChange against text the
        // workspace no longer holds.
        _session.InvalidateBaseline();
    }

    public void UpdateKind(MdRunKind kind)
    {
        if (!_layout.SetKind(kind)) return;
        // force re-sync with the new kind-transform prefix on the next analysis pass — same
        // baseline-invalidation reasoning as UpdateGlobalUsings above.
        _session.InvalidateBaseline();
    }

    // ── Document sync ──────────────────────────────────────────────────────────

    private Document SyncDocument(string code)
    {
        lock (_session.SyncLock)
        {
            _context.RequireContractSources(ConnectionContractAssemblyCache.RequiresSourceCompilation(code));
            return _session.Sync(code);
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
        => ScriptDocumentProjection.ComputeMinimalChange(oldText, newText);

    // ── Completion ─────────────────────────────────────────────────────────────

    public Task<ICompletionList?> GetCompletionsAsync(
        string code, int position, CompletionTrigger trigger = default, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (ICompletionList?)await TryGetCompletionsAsync(code, position, trigger, ct), ct);

    // A completion that read its solution before the references or generated classes changed lists
    // the old ones (a connection's members that are gone, none of the new): it is computed again on
    // the new solution, not shown. A few rounds at most, so a context that keeps changing cannot
    // keep a completion from ever answering.
    private async Task<ICompletionList?> TryGetCompletionsAsync(
        string code, int position, CompletionTrigger trigger, CancellationToken ct)
    {
        var version = new StrongBox<int>();
        for (var attempt = 0; ; attempt++)
        {
            var list = await ComputeCompletionsAsync(code, position, trigger, version, ct);
            if (attempt == MaxCompletionAttempts - 1 || IsCurrent(version.Value)) return list;
            ct.ThrowIfCancellationRequested();
        }
    }

    private const int MaxCompletionAttempts = 3;

    private async Task<ICompletionList?> ComputeCompletionsAsync(
        string code, int position, CompletionTrigger trigger, StrongBox<int> contextVersion, CancellationToken ct)
    {
        try
        {
            Document document;
            lock (_session.SyncLock)
            {
                document = SyncDocument(code);
                contextVersion.Value = _session.ContextVersion;
            }
            CompletionSnapshotTakenForTests?.Invoke();
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = _layout.ToDocumentOffset(code, position);
            return ProvideCompletions(new CompletionRequest(code, position, trigger, model, root, pos));
        }
        // A superseded request (see CodeTextEditor.ShowCompletion) cancels its token —
        // that's an expected outcome, not a failure, so it must propagate instead of being swallowed by
        // the general catch below (which would otherwise log it as a spurious warning on every fast
        // keystroke that supersedes an in-flight request).
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Completions failed"); return null; }
    }

    // Collapses same-named candidates down to one item per DisplayText (the same instance already
    // won by ScopeRank/Kind ordering everywhere this was called before), stamping the survivor with
    // OverloadCount when more than one Method-kind candidate shares the name (
    // without this, accepting a name with several overloads gave no hint there was anything else
    // under it at all) and with IsOwnMember if ANY grouped candidate is declared directly on the
    // current/receiver type — e.g. Derived.Foo(int) shadowing
    // Base.Foo(string) should still render bold even though ScopeRank/Kind ordering might pick the
    // base one as the group's representative.
    private static IReadOnlyList<ICompletionItem> GroupByDisplayText(IEnumerable<ICompletionItem> items) =>
        items
            .GroupBy(i => i.DisplayText)
            .Select(g =>
            {
                var chosen = g.OrderBy(i => (int)i.Kind).First();
                if (chosen is not SimpleCompletionItem simple) return chosen;

                var overloadCount = g.Count(i => i.Kind is CompletionItemKind.Method or CompletionItemKind.ExtensionMethod);
                var isOwnMember = g.Any(i => i.IsOwnMember);
                return simple with
                {
                    OverloadCount = overloadCount > 1 ? overloadCount : simple.OverloadCount,
                    IsOwnMember = isOwnMember,
                };
            })
            .ToList();

    // The INamedTypeSymbol containing the given position — used for the general (non-dot) completion
    // branch's "is this an own member" check. Walks up from
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

    // ── Override-member stubs ────────────────────────

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
            method.ToDisplayParts(SymbolFormats.Brief).ToSegments(), XmlDocSummary.GetSummary(method),
            SortText: method.Name, Priority: 1.0, UsingsToAdd: [], InsertTemplate: template,
            Source: CompletionSourceKind.Template);
    }

    // The nearest ObjectInitializerExpression ancestor of the token right before the
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
        //.
        IMethodSymbol m when m.MethodKind is MethodKind.ReducedExtension
            => BuildExtensionMethodItem(m, currentType),
        IMethodSymbol m when m.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction
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
        // generic method/type's own body, `T` is a real, commonly-referenced symbol,
        // not just declaration-site syntax.
        ITypeParameterSymbol tp
            => BuildItem(tp, tp.Name, CompletionItemKind.TypeParameter),
        // Namespaces were silently dropped here despite ScopeRank already carrying a rank for them
        // (3 — below types) — LookupSymbols(pos) does return INamespaceSymbol candidates (e.g. typing
        // "Sys" offers "System" in real VS/Rider), they just never became a completion item
        //. CompletionItemKind has no dedicated "Namespace" entry — Module is the
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
            symbol.ToDisplayParts(SymbolFormats.Brief).ToSegments(), XmlDocSummary.GetSummary(symbol),
            SortText: $"{rank}{displayText}", Priority: ScopeRankPriority(rank), UsingsToAdd: [],
            IsOwnMember: isOwnMember);
    }

    private static ICompletionItem BuildExtensionMethodItem(IMethodSymbol method, ITypeSymbol? currentType)
    {
        var item = (SimpleCompletionItem)BuildItem(method, method.Name, CompletionItemKind.ExtensionMethod,
            IsOwnMember(method, currentType));
        // Invoklet's own extensions (Dump and the like) win a tie with an equally matching member: typing
        // `.D` on any value most often means Dump. The list order itself stays alphabetical.
        return IsInvokletRuntimeMember(method)
            ? item with { Priority = Math.Max(1.1, item.Priority), Source = CompletionSourceKind.Invoklet }
            : item with { Priority = Math.Max(0.85, item.Priority) };
    }

    private static bool IsInvokletRuntimeMember(ISymbol symbol) =>
        symbol.ContainingAssembly?.Name == "Invoklet.Runtime";

    // A type from a reference that isn't in
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
        // the list and add an empty entry to the .inv front matter's namespaces (later producing an
        // invalid "using ;").
        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();

        const int rank = 3; // own tier, see ScopeRank
        return new SimpleCompletionItem(
            type.Name, kind, type.ToDisplayParts(SymbolFormats.Brief).ToSegments(), XmlDocSummary.GetSummary(type),
            SortText: $"{rank}{type.Name}", Priority: ScopeRankPriority(rank),
            UsingsToAdd: ns is null ? [] : [ns], Detail: ns, Source: CompletionSourceKind.ImportableType);
    }

    // Types from the script's references whose simple name starts with what's typed so far and
    // isn't already visible: packages and the script's own assemblies from the first letter, the
    // framework from MinFrameworkImportPrefix letters, so a letter or two doesn't list half the
    // framework. Types that share a name are all offered, each with its own namespace: the user
    // picks which `using` to add, none is chosen for them.
    private IEnumerable<INamedTypeSymbol> FindImportableTypes(
        Compilation compilation, string prefix, HashSet<string> alreadyVisible)
    {
        if (prefix.Length == 0) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in compilation.References)
        {
            if (prefix.Length < MinFrameworkImportPrefix && !_context.IsAddedReference(reference)) continue;
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;

            foreach (var entry in GetImportableTypeIndex(assembly, reference))
            {
                if (alreadyVisible.Contains(entry.Name)) continue;
                if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // The same type can come from two references (a package that also ships in the framework).
                if (!seen.Add(entry.MetadataName)) continue;

                var type = assembly.GetTypeByMetadataName(entry.MetadataName);
                if (type == null || IsHiddenFromEditor(type)) continue;
                yield return type;
            }
        }
    }

    private const int MinFrameworkImportPrefix = 3;

    // [EditorBrowsable(Never)]: public for technical reasons, not meant to be written by hand.
    private static bool IsHiddenFromEditor(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == nameof(System.ComponentModel.EditorBrowsableAttribute)
            && a.ConstructorArguments is [{ Value: int state }] && state == (int)System.ComponentModel.EditorBrowsableState.Never);

    // Flattened index of every public importable type in a referenced assembly, built once per
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
    // BuildImportableTypeItem), which
    // sort before namespaces — so typing a few letters shows what's actually nearby before it shows
    // something merely imported/visible, and before something that isn't even imported yet
    // ("the in-scope thing first, then a level up").
    private static int ScopeRank(ISymbol symbol) => symbol switch
    {
        ILocalSymbol or IParameterSymbol => 0,
        IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol => 1,
        INamedTypeSymbol => 2,
        INamespaceSymbol => 4,
        _ => 2,
    };

    // Whether a member found via LookupSymbols(container: type) actually belongs on the side of the
    // dot it was requested for — "DateTime." (static qualifier) vs "d." (instance qualifier).
    // Nested types are only ever reachable through the type itself (there's no such
    // thing as `instance.NestedType`), regardless of the nested type's own staticness, hence the
    // special case rather than just comparing symbol.IsStatic for everything.
    private static bool MatchesQualifierStaticness(ISymbol symbol, bool isStaticQualifier) => symbol switch
    {
        INamedTypeSymbol => isStaticQualifier,
        _ => symbol.IsStatic == isStaticQualifier,
    };

    // Generated script APIs are intentionally treated as first-class completion surfaces.  Their
    // members come from the current execution context (Arguments/Connections) or the compatibility
    // runtime (Util), so they should win ties against similarly named ordinary symbols while still
    // retaining the normal alphabetical order inside this member list.
    private static bool IsGeneratedApiRoot(ExpressionSyntax qualifier) =>
        qualifier is IdentifierNameSyntax { Identifier.ValueText: "Arguments" or "Connections" or "Util" };

    private static ICompletionItem PromoteGeneratedApiItem(ICompletionItem item) => item switch
    {
        SimpleCompletionItem simple => simple with
        {
            SortText = $"0{simple.SortText}",
            Priority = Math.Max(1.1, simple.Priority),
            Source = CompletionSourceKind.Invoklet,
        },
        _ => item,
    };

    // ICompletionItem.Priority — the editor's tie-break among items of otherwise-equal match quality
    // (see AvaloniaEdit.CodeCompletion.CompletionList.SelectItem: quality decides first, Priority only
    // breaks ties). Derived from the same ScopeRank used for SortText, rather than the UI layer's
    // former Kind == Method ? 1.0 : 0.9 hardcode — so a local sitting right next to
    // an equally-good-matching method actually wins the tie-break its ScopeRank already implies.
    private static double ScopeRankPriority(int rank) => rank switch
    {
        0 => 1.0,  // locals/parameters
        1 => 0.8,  // members
        2 => 0.6,  // types
        3 => 0.5,  // importable (not-yet-imported) types
        _ => 0.4,  // namespaces
    };

    public Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code, int position, char? triggerCharacter = null, CancellationToken ct = default)
        => _signatureHelp.GetSignatureHelpAsync(code, position, triggerCharacter, ct);

    // ── Quick Info, highlights, rename, go to definition and implementation ─────────────

    public Task<IQuickInfoResult?> GetQuickInfoAsync(string code, int position, CancellationToken ct = default)
        => _navigation.GetQuickInfoAsync(code, position, ct);

    public Task<IReadOnlyList<IDocumentHighlightSpan>?> GetDocumentHighlightsAsync(
        string code, int position, CancellationToken ct = default)
        => _navigation.GetDocumentHighlightsAsync(code, position, ct);

    public Task<IRenameInfo?> GetRenameInfoAsync(string code, int position, CancellationToken ct = default)
        => _navigation.GetRenameInfoAsync(code, position, ct);

    public Task<IDefinitionResult?> GetDefinitionAsync(string code, int position, CancellationToken ct = default)
        => _navigation.GetDefinitionAsync(code, position, ct);

    public Task<IReadOnlyList<IImplementationLocation>?> GetImplementationsAsync(
        string code, int position, CancellationToken ct = default)
        => _navigation.GetImplementationsAsync(code, position, ct);

    // ── Breakpoint conditions and format ─────────────────────────────────────────────────────────────────

    public Task<BreakpointConditionAnalysis?> AnalyzeBreakpointConditionAsync(string code, int position,
        string expression, CancellationToken cancellationToken = default)
        => _breakpointConditions.AnalyzeBreakpointConditionAsync(code, position, expression, cancellationToken);

    public Task<string?> FormatDocumentAsync(string code, CancellationToken ct = default) => _formatter.FormatDocumentAsync(code, ct);

    public Task<bool> IsEmptyTypeBodyPositionAsync(string code, int position, CancellationToken cancellationToken = default)
        => Task.Run(() => CSharpMemberPosition.IsInEmptyTypeBody(code, position, cancellationToken), cancellationToken);

    public void UpdateIndentation(bool useTabs, int indentationSize) => _session.UpdateIndentation(useTabs, indentationSize);

    // ── Refactorings (CodeRefactoringProvider hosting in the lightbulb) ─────────────

    public Task<IReadOnlyList<ICodeFixInfo>?> GetRefactoringsAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IReadOnlyList<ICodeFixInfo>?)await TryGetRefactoringsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<ICodeFixInfo>?> TryGetRefactoringsAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            if (position < 0 || position > code.Length) return null;

            var document = SyncDocument(code);
            var headerLength = _layout.HeaderLength(code);
            var span = new TextSpan(_layout.ToDocumentOffset(code, position), 0);

            // 1-based, same convention as CodeFixEntry.Line/RoslynCodeFixEngine's own diagnostic-fix
            // line lookup — LinePosition.Line itself is 0-based.
            var userLine = SourceText.From(code).Lines.GetLinePosition(position).Line + 1;

            return await _codeFixEngine.ComputeRefactoringsAsync(document, span, _layout.Projection(code).Text, headerLength, userLine, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRefactoringsAsync failed"); return null; }
    }

    // The deferred half of
    // both CodeFixesChanged's fixes and GetRefactoringsAsync's refactorings, see IIntelliSenseService.
    // MaterializeFixAsync's own doc comment. No SyncDocument call here — the cached CodeAction
    // RoslynCodeFixEngine looks key up already carries its own document snapshot internally (captured
    // when it was registered), so materializing it needs nothing from the live workspace at all, only
    // the code/preamble metrics needed for the relocating-preamble math.
    public Task<IMaterializedFix?> MaterializeFixAsync(string code, string key, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IMaterializedFix?)await TryMaterializeFixAsync(code, key, ct), ct);

    private async Task<IMaterializedFix?> TryMaterializeFixAsync(string code, string key, CancellationToken ct)
    {
        try
        {
            var headerLength = _layout.HeaderLength(code);
            var materialized = await _codeFixEngine.MaterializeAsync(key, _layout.Projection(code).Text, headerLength, ct);
            if (materialized is null) return null;
            var projection = _layout.Projection(code);
            if (projection.RestoreEdit(materialized.Value.NewCode, materialized.Value.ChangedSpans,
                    _layout.Prefix(code), _layout.Suffix(code), _layout.ParseOptions) is not var (restored, changedSpans)) return null;
            var newCode = restored;
            if (_layout.Kind == MdRunKind.Program && restored != code)
            {
                // A class compiled outside the hidden one was formatted at its own columns already.
                var moved = projection.HasChanges ? ProgramBodyProjection.Create(restored, _layout.ParseOptions).Moved : [];
                newCode = await ReindentOutsideTheWrapperAsync(restored,
                    [.. changedSpans.Where(span => !moved.Any(m => m.Contains(span)))], ct);
            }
            return new SimpleMaterializedFix(newCode, materialized.Value.UsingsToAdd);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "MaterializeFixAsync failed"); return null; }
    }

    // The Program kind analyses the user's members inside the hidden wrapper class while they sit at
    // column 0 of the editor, so the formatter behind a code action indents what it writes one level
    // deeper than the visible code (braces under an `if`, a member's own line, a wrapped expression).
    // Only the lines the action's own text changes touch are corrected, found from those changes
    // rather than by comparing the whole script, so a large script costs what its text costs.
    //  - A continuation line the action broke off (its line break and indentation are the action's
    //    own text) loses the extra level, unless it starts at a token column of the line above:
    //    then the action aligned it there, and the column is the same in the editor.
    //  - The touched lines are then formatted again where the wrapper and the user's columns agree —
    //    every line one level in, inside a class — and that level is taken off again. Lines aligned
    //    to a token stay aligned; lines the action left alone are not touched.
    internal async Task<string> ReindentOutsideTheWrapperAsync(string newCode, IReadOnlyList<TextSpan> changedSpans, CancellationToken ct)
    {
        if (changedSpans.Count == 0) return newCode;

        OptionSet options;
        lock (_session.SyncLock) options = _session.FormattingOptionsSnapshot();
        var useTabs = options.GetOption(FormattingOptions.UseTabs, LanguageNames.CSharp);
        var unit = useTabs ? "\t" : new string(' ', options.GetOption(FormattingOptions.IndentationSize, LanguageNames.CSharp));
        CSharpParseOptions parseOptions;
        lock (_session.SyncLock) parseOptions = _layout.ParseOptions;

        var lines = newCode.Split('\n');
        var lineStarts = new int[lines.Length];
        for (int i = 0, offset = 0; i < lines.Length; offset += lines[i].Length + 1, i++)
            lineStarts[i] = offset;

        var touched = new SortedSet<int>();
        foreach (var span in changedSpans)
        {
            ct.ThrowIfCancellationRequested();
            var first = LineOf(lineStarts, span.Start);
            var last = LineOf(lineStarts, Math.Max(span.Start, span.End - 1));
            for (var line = first; line <= last; line++)
            {
                touched.Add(line);
                if (!useTabs && line > 0 && lineStarts[line] > span.Start && lineStarts[line] <= span.End)
                    lines[line] = WithoutWrapperLevel(lines[line], lines[line - 1], unit);
            }
        }

        const string header = "class __InvokletIndent\n{\n";
        var text = new StringBuilder(header, header.Length + newCode.Length + lines.Length * unit.Length + 4);
        var spans = new List<TextSpan>(touched.Count);
        for (var i = 0; i < lines.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var start = text.Length;
            if (lines[i].Length > 0) text.Append(unit);
            text.Append(lines[i]);
            if (touched.Contains(i)) spans.Add(TextSpan.FromBounds(start, text.Length));
            if (i < lines.Length - 1) text.Append('\n');
        }
        text.Append("\n}\n");

        using var formatWorkspace = new AdhocWorkspace(InvokletRoslynHost.HostServices);
        var projectId = ProjectId.CreateNewId("Indent");
        formatWorkspace.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Indent", "Indent",
            LanguageNames.CSharp, parseOptions: parseOptions));
        var document = formatWorkspace.AddDocument(projectId, "Script.cs", SourceText.From(text.ToString(), Encoding.UTF8));
        var formatted = await Formatter.FormatAsync(document, spans, options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n"), ct);
        var result = (await formatted.GetTextAsync(ct)).ToString();
        if (!result.StartsWith(header, StringComparison.Ordinal) || !result.EndsWith("\n}\n", StringComparison.Ordinal))
            return newCode;

        var body = result[header.Length..^"\n}\n".Length].Split('\n');
        return string.Join('\n', body.Select(l => l.StartsWith(unit, StringComparison.Ordinal) ? l[unit.Length..] : l));
    }

    private static int LineOf(int[] lineStarts, int position)
    {
        var index = Array.BinarySearch(lineStarts, position);
        return index >= 0 ? index : ~index - 1;
    }

    // One indentation level less for a continuation line of spaces, unless its first character sits
    // under the start of a token of the line above (an alignment).
    private static string WithoutWrapperLevel(string line, string above, string unit)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent < unit.Length || indent == line.Length || line[indent] == '\t') return line;
        var alignedToToken = indent < above.Length && !char.IsWhiteSpace(above[indent])
            && (char.IsWhiteSpace(above[indent - 1]) || !char.IsLetterOrDigit(above[indent - 1]) || !char.IsLetterOrDigit(above[indent]));
        return alignedToToken ? line : line[unit.Length..];
    }

    // ReSharper-style "resolve all missing usings" — see IIntelliSenseService's own doc comment for
    // the contract. Only compiler diagnostics (never analyzer diagnostics — see
    // ComputeDiagnosticsAndCodeFixesAsync's own note on why those need a separate GetCompilationAsync
    // call this doesn't need) are relevant: CS0246 ("type or namespace not found") and CS0103 ("name
    // does not exist") are the two Roslyn actually attaches an "Add using X;" fix to.
    public Task<IMaterializedFix> ResolveAllMissingUsingsAsync(string code, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IMaterializedFix)await TryResolveAllMissingUsingsAsync(code, ct), ct);

    private async Task<IMaterializedFix> TryResolveAllMissingUsingsAsync(string code, CancellationToken ct)
    {
        try
        {
            var document = SyncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model is null) return new SimpleMaterializedFix(code, []);

            var headerLength = _layout.HeaderLength(code);
            var diagnostics = model.GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Id is "CS0246" or "CS0103")
                .ToList();

            var usings = await _codeFixEngine.ResolveAllMissingUsingsAsync(document, diagnostics, _layout.Projection(code).Text, headerLength, ct);
            return new SimpleMaterializedFix(code, usings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResolveAllMissingUsingsAsync failed");
            return new SimpleMaterializedFix(code, []);
        }
    }

    // ── Background analysis (diagnostics + semantic tokens) ───────────────────

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;
    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;
    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;
    public event Action<string, IReadOnlyList<IInlayHint>>? InlayHintsChanged;

    public void NotifyTextChanged(string code) => NotifyTextChanged(() => code);

    // The code is materialized only here, after the debounce and off the caller's thread — a
    // notification superseded within BackgroundAnalysisDebounce (the next keystroke) never builds its
    // string at all.
    public void NotifyTextChanged(Func<string> getCode) => _scheduler.Notify(getCode, AnalyzeAsync);

    // One background pass over the text; false when the project context changed meanwhile, which makes what it found describe the
    // old references or generated classes (it was not published): the scheduler runs it again on the new solution, unless a newer
    // notification has taken over already.
    private async Task<bool> AnalyzeAsync(string code, CancellationToken ct)
    {
        Document document;
        int version;
        lock (_session.SyncLock)
        {
            document = SyncDocument(code);
            version = _session.ContextVersion;
        }
        AnalysisSnapshotTakenForTests?.Invoke();
        await Task.WhenAll(
            ComputeDiagnosticsAndCodeFixesAsync(code, document, version, ct),
            ComputeSemanticTokensAsync(code, document, version, ct),
            ComputeInlayHintsAsync(code, document, version, ct));
        return IsCurrent(version);
    }

    private bool IsCurrent(int version) => _session.IsCurrent(version);

    // Whether a result may be published: a newer text notification cancels the pass, and a change to
    // the project's context makes its solution obsolete. Checked right before each event, since the
    // analysis itself may finish after either.
    private bool CanPublish(int version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return IsCurrent(version);
    }

    private async Task ComputeSemanticTokensAsync(string code, Document document, int version, CancellationToken ct)
    {
        var headerLength = _layout.HeaderLength(code);
        var textLen = headerLength + _layout.Projection(code).Text.Length;
        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, textLen), ct);

        var result = new List<ISemanticToken>();
        foreach (var span in spans)
        {
            var kind = ClassifySpan(span.ClassificationType);
            if (kind == SemanticTokenKind.None) continue;

            var mapped = _layout.ToUserSpan(code, span.TextSpan);
            if (mapped is null || mapped.Value.IsEmpty) continue;
            var start = mapped.Value.Start;
            var end = mapped.Value.End;

            result.Add(new SimpleSemanticToken(start, end - start, kind));
        }

        if (CanPublish(version, ct)) SemanticTokensChanged?.Invoke(code, result);
    }

    // Inferred `var x` / `foreach (var x in ...)` type annotations only
    // (Roslyn's own CSharpInlineTypeHintsService also covers lambda parameters, implicit `new()`, and
    // collection expressions — internal API, decompiled to confirm exact placement/format below, not
    // ported wholesale; those three are lower-value, left for a follow-up if wanted). Single-variable
    // declarations only (`var a = 1, b = 2;` skipped entirely) — matches Roslyn's own behavior; a
    // multi-declarator statement can only have one inferred type anyway, so hinting just the first
    // variable would misleadingly suggest it doesn't apply to the rest.
    private async Task ComputeInlayHintsAsync(string code, Document document, int version, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (model is null || root is null)
        {
            if (CanPublish(version, ct)) InlayHintsChanged?.Invoke(code, []);
            return;
        }

        var headerLength = _layout.HeaderLength(code);
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

        if (CanPublish(version, ct)) InlayHintsChanged?.Invoke(code, result);

        // Roslyn's own CSharpInlineTypeHintsService (IsValidType) only excludes null and a type
        // literally named "var" (e.g. a user-defined `class var` — legal, if unusual, C#); it doesn't
        // special-case error types beyond that, and neither does this.
        void AddHint(List<IInlayHint> hints, ITypeSymbol? type, int identifierPos, int typeSyntaxPos,
            int headerLength, string code, SemanticModel model)
        {
            if (type is null || type.Name == "var") return;

            var offset = _layout.ToUserOffset(code, identifierPos);
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
    // on every 500ms background tick.
    private async Task ComputeDiagnosticsAndCodeFixesAsync(string code, Document document, int version, CancellationToken ct)
    {
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null)
        {
            if (!CanPublish(version, ct)) return;
            DiagnosticsChanged?.Invoke(code, []);
            CodeFixesChanged?.Invoke(code, []);
            return;
        }

        // Roslyn's own IDE-style analyzers ("use var", "make
        // readonly", ...) alongside the compiler's own diagnostics. GetCompilationAsync is the same
        // cached compilation model.GetDiagnostics() already reads from (Roslyn caches per Project
        // version), not a second build.
        var compilation = await document.Project.GetCompilationAsync(ct);
        var analyzerDiagnostics = compilation is null
            ? []
            : await _analyzerEngine.ComputeAsync(compilation, _InvokletAnalyzersEnabled, ct);

        var headerLength = _layout.HeaderLength(code);
        var visible = new List<IDiagnosticInfo>();
        var forCodeFixes = new List<Diagnostic>();
        foreach (var d in model.GetDiagnostics(cancellationToken: ct).Concat(analyzerDiagnostics))
        {
            // The analysis workspace is a library to allow incomplete/class-only edits, while
            // scripts are compiled as executables. Its library-only top-level error is artificial.
            if (d.Id == "CS8805") continue;

            // The analyzers see the whole compilation, the generated Connections documents included;
            // offsets there mean nothing in the script, so neither the squiggles nor the code fixes
            // (which map every offset onto the user's text) may take them.
            if (!_context.IsInScript(d.Location)) continue;

            // Unlike compiler diagnostics (never actually Hidden in practice), most of the analyzer
            // diagnostics above fire at Hidden severity BY DESIGN — no squiggle, only a CodeFixProvider
            // keyed to that same diagnostic ID becoming available (RoslynAnalyzerEngine's own doc
            // comment). Still added to forCodeFixes either way — only excluded from the visible list.
            forCodeFixes.Add(d);
            if (d.Severity == RoslynSeverity.Hidden) continue;

            var span = d.Location.SourceSpan;
            var mappedStart = _layout.ToUserSpan(code, span);
            if (mappedStart is null) continue;
            var start = mappedStart.Value.Start;

            // Clamp to code.Length before the start>=end skip below, same pattern as
            // ComputeSemanticTokensAsync/ComputeInlayHintsAsync's own bounds checks — needed since
            // MdRunKind.Program's mirrored KindSuffix (the wrapper class's closing brace)
            // now follows the user's own code in the synced document, so a diagnostic anchored there
            // (or straddling into it) needs to not leak an out-of-range span to the editor.
            var end = Math.Min(Math.Max(mappedStart.Value.End, start + 1), code.Length);
            if (start >= end) continue;

            var severity = d.Severity switch
            {
                RoslynSeverity.Error => ContractSeverity.Error,
                RoslynSeverity.Warning => ContractSeverity.Warning,
                _ => ContractSeverity.Info,
            };
            visible.Add(new SimpleDiagnostic(start, end, d.GetMessage(RoslynMessageCulture.Culture), severity));
        }

        // Diagnostics the source generators report themselves (an invalid [GeneratedRegex] pattern, a
        // member System.Text.Json can't see) are part of the build's output but of no semantic model.
        // Only those inside the user's own text are shown; none take part in code fixes.
        if (compilation is not null)
        {
            var seen = new HashSet<(string, int, int)>();
            foreach (var (d, span) in await GeneratorDiagnosticsAsync(document, compilation, model.SyntaxTree, ct))
            {
                if (d.Severity is RoslynSeverity.Hidden or RoslynSeverity.Info) continue;
                var mapped = _layout.ToUserSpan(code, span);
                if (mapped is null) continue;
                var start = mapped.Value.Start;
                var end = Math.Min(Math.Max(mapped.Value.End, start + 1), code.Length);
                if (start < 0 || start >= end || !seen.Add((d.Id, start, end))) continue;
                visible.Add(new SimpleDiagnostic(start, end, d.GetMessage(RoslynMessageCulture.Culture),
                    d.Severity == RoslynSeverity.Error ? ContractSeverity.Error : ContractSeverity.Warning));
            }
        }

        if (!CanPublish(version, ct)) return;
        DiagnosticsChanged?.Invoke(code, visible);

        var fixes = await _codeFixEngine.ComputeFixesAsync(document, forCodeFixes, _layout.Projection(code).Text, headerLength, ct);
        if (CanPublish(version, ct)) CodeFixesChanged?.Invoke(code, fixes);
    }

    // The workspace runs the project's generators but keeps what they report to itself, so they are
    // run once more for that, on the compilation without their output: the same instances and parse
    // options, cancelled with the analysis pass. A location is used only when it is in the user's
    // document — generators report at external-file locations naming that document's path — never
    // in generated code, which isn't part of that compilation.
    private static async Task<List<(Diagnostic Diagnostic, TextSpan Span)>> GeneratorDiagnosticsAsync(
        Document document, Compilation compilation, SyntaxTree userTree, CancellationToken ct)
    {
        var generators = document.Project.AnalyzerReferences
            .SelectMany(r => r.GetGenerators(LanguageNames.CSharp)).ToImmutableArray();
        if (generators.IsEmpty) return [];

        var generated = await document.Project.GetSourceGeneratedDocumentsAsync(ct);
        var generatedTrees = new List<SyntaxTree>();
        foreach (var g in generated)
            generatedTrees.Add(await g.GetSyntaxTreeAsync(ct) ?? throw new InvalidOperationException());
        var withoutGenerated = compilation.RemoveSyntaxTrees(generatedTrees);

        var run = CSharpGeneratorDriver
            .Create(generators, parseOptions: (CSharpParseOptions?)document.Project.ParseOptions)
            .RunGenerators(withoutGenerated, ct)
            .GetRunResult();

        var result = new List<(Diagnostic, TextSpan)>();
        foreach (var d in run.Diagnostics)
        {
            var location = d.Location;
            var inUserTree = location.IsInSource
                ? location.SourceTree == userTree
                : location.Kind == LocationKind.ExternalFile && location.GetLineSpan().Path == userTree.FilePath;
            if (inUserTree && location.SourceSpan.End <= userTree.Length)
                result.Add((d, location.SourceSpan));
        }
        return result;
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

    /// <summary>Updates the typed <c>Arguments</c> members offered by completion.</summary>
    public void UpdateArgumentContract(MdRunArgumentContract? contract) => _context.UpdateArgumentContract(contract);

    public void UpdateExecutionContext(ExecutionRequest request) => _context.UpdateExecutionContext(request);

    public void ApplyExecutionContext(ExecutionRequest request, CancellationToken cancellationToken)
        => _context.ApplyExecutionContext(request, cancellationToken);

    // Without a build context: the runtime IntelliSense itself runs on, the user's NuGet settings.
    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages) => _context.UpdatePackageReferences(packages);

    public void UpdatePackageReferences(IReadOnlyDictionary<string, Package> packages, PackageReferenceContext context)
        => _context.UpdatePackageReferences(packages, context);

    internal Action<ExecutionRequest>? ExecutionContextResolvedForTests
    {
        get => _context.ExecutionContextResolvedForTests;
        set => _context.ExecutionContextResolvedForTests = value;
    }

    // Test-only (InternalsVisibleTo): the package assemblies resolved for the packages — the workspace
    // references them unless the framework provides the same assembly at a higher version.
    internal IReadOnlyList<string> PackageReferencePathsForTests
    {
        get => _context.PackageReferencePaths;
    }

    // Test-only (InternalsVisibleTo): the source generator assemblies the workspace runs.
    internal IReadOnlyList<string> GeneratorPathsForTests
    {
        get
        {
            lock (_session.SyncLock)
                return _session.Workspace.CurrentSolution.GetProject(_session.DocumentId.ProjectId)!.AnalyzerReferences
                    .Select(r => r.FullPath!).ToList();
        }
    }

    // Test-only (InternalsVisibleTo): every assembly the workspace currently references.
    internal IReadOnlyList<string> ReferencePathsForTests
    {
        get
        {
            lock (_session.SyncLock)
                return _session.Workspace.CurrentSolution.GetProject(_session.DocumentId.ProjectId)!.MetadataReferences
                    .OfType<PortableExecutableReference>().Select(r => r.FilePath!).ToList();
        }
    }

    // Cancel() alone only *requests* cooperative cancellation — NotifyTextChanged's Task.Run body can
    // still be mid-flight (e.g. deep inside RoslynCodeFixEngine.ComputeFixesAsync, which doesn't poll
    // the token between individual CodeFixProviders) when this method would otherwise return
    // immediately, racing _session.Workspace.Dispose() right below and continuing to burn CPU on a ThreadPool
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
        _scheduler.Stop();
        _session.Workspace.Dispose();
    }

    // ── Private model types ────────────────────────────────────────────────────

    private sealed record SimpleDiagnostic(
        int StartOffset, int EndOffset, string Message,
        ContractSeverity Severity) : IDiagnosticInfo;

    private sealed record SimpleSemanticToken(
        int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;

    private sealed record SimpleInlayHint(int Offset, string Text) : IInlayHint;

    private sealed record SimpleMaterializedFix(string NewCode, IReadOnlyList<string> UsingsToAdd) : IMaterializedFix;

}
