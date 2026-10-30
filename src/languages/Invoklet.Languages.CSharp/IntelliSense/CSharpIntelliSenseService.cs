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
    private readonly RefactoringService _refactorings;
    private readonly BackgroundAnalysis _analysis;
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

    // Test-only: called by a completion once it holds its document snapshot, before it reads it.
    internal Action? AnalysisSnapshotTakenForTests
    {
        get => _analysis.SnapshotTakenForTests;
        set => _analysis.SnapshotTakenForTests = value;
    }

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
        _codeFixEngine = new RoslynCodeFixEngine(InvokletRoslynHost.CompositionHost, logger);
        _analyzerEngine = new RoslynAnalyzerEngine(logger);
        _navigation = new SymbolNavigation(SyncDocument, _layout, _context, logger);
        _signatureHelp = new SignatureHelp(SyncDocument, _layout, logger);
        _breakpointConditions = new BreakpointConditionAnalyzer(SyncDocument, _layout);
        _formatter = new DocumentFormatter(_session, _layout, logger);
        _refactorings = new RefactoringService(_session, SyncDocument, _layout, _codeFixEngine, logger);
        _analysis = new BackgroundAnalysis(_session, SyncDocument, _layout, _context, _codeFixEngine, _analyzerEngine, () => _InvokletAnalyzersEnabled);
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
            if (attempt == MaxCompletionAttempts - 1 || _session.IsCurrent(version.Value)) return list;
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

    // ── Refactorings, fixes and missing usings ─────────────

    public Task<IReadOnlyList<ICodeFixInfo>?> GetRefactoringsAsync(string code, int position, CancellationToken ct = default)
        => _refactorings.GetRefactoringsAsync(code, position, ct);

    public Task<IMaterializedFix?> MaterializeFixAsync(string code, string key, CancellationToken ct = default)
        => _refactorings.MaterializeFixAsync(code, key, ct);

    internal Task<string> ReindentOutsideTheWrapperAsync(string newCode, IReadOnlyList<TextSpan> changedSpans, CancellationToken ct)
        => _refactorings.ReindentOutsideTheWrapperAsync(newCode, changedSpans, ct);

    public Task<IMaterializedFix> ResolveAllMissingUsingsAsync(string code, CancellationToken ct = default)
        => _refactorings.ResolveAllMissingUsingsAsync(code, ct);

    // ── Background analysis (diagnostics + semantic tokens) ───────────────────

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged
    {
        add => _analysis.DiagnosticsChanged += value;
        remove => _analysis.DiagnosticsChanged -= value;
    }

    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged
    {
        add => _analysis.SemanticTokensChanged += value;
        remove => _analysis.SemanticTokensChanged -= value;
    }

    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged
    {
        add => _analysis.CodeFixesChanged += value;
        remove => _analysis.CodeFixesChanged -= value;
    }

    public event Action<string, IReadOnlyList<IInlayHint>>? InlayHintsChanged
    {
        add => _analysis.InlayHintsChanged += value;
        remove => _analysis.InlayHintsChanged -= value;
    }

    public void NotifyTextChanged(string code) => NotifyTextChanged(() => code);

    // The code is materialized only here, after the debounce and off the caller's thread — a
    // notification superseded within BackgroundAnalysisDebounce (the next keystroke) never builds its
    // string at all.
    public void NotifyTextChanged(Func<string> getCode) => _scheduler.Notify(getCode, _analysis.AnalyzeAsync);

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


}
