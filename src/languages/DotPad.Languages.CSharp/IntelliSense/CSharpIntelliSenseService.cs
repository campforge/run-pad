using System.Collections.Concurrent;
using System.Text;
using DotPad.Contracts.IntelliSense;
using DotPad.Format.RPad;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    private static readonly string[] _tfmPreference =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0",
        "netcoreapp3.1", "netstandard2.1", "netstandard2.0",
    ];

    private readonly ILogger<CSharpIntelliSenseService> _logger;
    private readonly AdhocWorkspace _workspace;
    private readonly DocumentId _documentId;
    private readonly string _nugetRoot = NuGetPaths.GetGlobalPackagesFolder();
    private readonly List<MetadataReference> _nugetRefs = [];
    private readonly RoslynCodeFixEngine _codeFixEngine;
    private string[] _extraPreambleUsings = [];
    private string _preamble = "";
    private int _preambleLength;
    private string _lastCode = "";
    // Guards _lastCode and the AdhocWorkspace's CurrentSolution against concurrent SyncDocument
    // calls — completion/signature-help/quick-info/background-analysis each run on their own
    // Task.Run, so without this a completion request for the text at keystroke N could race a
    // still-in-flight background-analysis pass for keystroke N-2 (ANALYSIS.md TODO #56).
    private readonly object _syncLock = new();
    private CancellationTokenSource? _diagnosticsCts;

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
        _codeFixEngine = new RoslynCodeFixEngine(DotPadRoslynHost.CompositionHost, logger);

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
        // force re-sync with the new preamble on the next analysis pass
        lock (_syncLock) { _lastCode = ""; }
    }

    // ── Document sync ──────────────────────────────────────────────────────────

    private Document SyncDocument(string code)
    {
        lock (_syncLock)
        {
            if (_lastCode != code)
            {
                var text = SourceText.From(_preamble + code, Encoding.UTF8);
                var newSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, text);
                // Only remember the new code once the workspace actually reflects it — TryApplyChanges
                // can fail (e.g. a solution-version conflict from a concurrent update elsewhere), and
                // updating _lastCode unconditionally used to mean a failed apply left CurrentSolution
                // showing stale text while _lastCode claimed it was already in sync, permanently
                // skipping the sync on every later call with that same code.
                if (_workspace.TryApplyChanges(newSolution))
                    _lastCode = code;
            }
            return _workspace.CurrentSolution.GetDocument(_documentId)!;
        }
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

            var pos = position + _preambleLength;
            if (IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos)) return null;

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
                        var nsItems = model.LookupNamespacesAndTypes(pos, container: containingNamespace)
                            .Select(ToItem)
                            .Where(i => i != null)
                            .Cast<ICompletionItem>()
                            .GroupBy(i => i.DisplayText)
                            .Select(g => g.OrderBy(i => (int)i.Kind).First())
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
                        var memberItems = model
                            .LookupSymbols(pos, container: type, includeReducedExtensionMethods: !isStaticQualifier)
                            .Where(s => MatchesQualifierStaticness(s, isStaticQualifier))
                            .Select(ToItem)
                            .Where(i => i != null)
                            .Cast<ICompletionItem>()
                            .GroupBy(i => i.DisplayText)
                            .Select(g => g.OrderBy(i => (int)i.Kind).First())
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

            var symbols = model.LookupSymbols(pos);
            var visible = symbols
                .Select(ToItem)
                .Where(i => i != null)
                .Cast<ICompletionItem>()
                .Concat(_keywords)
                .GroupBy(i => i.DisplayText)
                .Select(g => g.OrderBy(i => (int)i.Kind).First())
                .ToList();

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

    private static ICompletionItem? ToItem(ISymbol symbol) => symbol switch
    {
        // LocalFunction alongside Ordinary/ReducedExtension — top-level statements (this project's
        // main script shape) use local functions as their primary way to structure code, so leaving
        // them out meant the single most common non-trivial identifier in a script never completed
        // (ANALYSIS.md #52).
        IMethodSymbol m when m.MethodKind is MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.LocalFunction
            => BuildItem(m, m.Name, CompletionItemKind.Method),
        IPropertySymbol p
            => BuildItem(p, p.Name, CompletionItemKind.Property),
        IFieldSymbol f when !f.IsImplicitlyDeclared
            => BuildItem(f, f.Name,
                f.ContainingType?.TypeKind == TypeKind.Enum ? CompletionItemKind.EnumMember : CompletionItemKind.Field),
        IEventSymbol e
            => BuildItem(e, e.Name, CompletionItemKind.Event),
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

    private static ICompletionItem BuildItem(ISymbol symbol, string displayText, CompletionItemKind kind)
    {
        var rank = ScopeRank(symbol);
        return new SimpleCompletionItem(displayText, kind,
            symbol.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(symbol),
            SortText: $"{rank}{displayText}", Priority: ScopeRankPriority(rank), UsingsToAdd: []);
    }

    // TODO.md #28 remainder — a type from an added NuGet package that isn't in scope yet. Ranked the
    // same as an ordinary type (ScopeRank 2); UsingsToAdd is what tells EditorTabItemControl to add
    // the `using` automatically when this item is accepted (see CompletionRequestedEventArgs's
    // consumer in EditorTabItemControl.AsyncCompletionProvider).
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
        var ns = type.ContainingNamespace.ToDisplayString();
        var summary = XmlDocSummary.GetSummary(type);
        var requiresNote = $"Requires \"using {ns};\"";
        var combinedSummary = string.IsNullOrEmpty(summary) ? requiresNote : $"{summary}\n\n{requiresNote}";

        const int rank = 2; // same tier as other types, see ScopeRank
        return new SimpleCompletionItem(
            type.Name, kind, type.ToDisplayParts(_briefFormat).ToSegments(), combinedSummary,
            SortText: $"{rank}{type.Name}", Priority: ScopeRankPriority(rank), UsingsToAdd: [ns]);
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

            foreach (var type in EnumerateNamespaceTypes(assembly.GlobalNamespace))
            {
                if (alreadyVisible.Contains(type.Name)) continue;
                if (!type.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                // First sighting of this name -> tentatively importable; a second, different type
                // with the same name makes it ambiguous -> null marks it as excluded.
                byName[type.Name] = byName.TryGetValue(type.Name, out var existing) && existing != null
                    ? null
                    : type;
            }
        }

        foreach (var type in byName.Values)
            if (type != null)
                yield return type;
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
    // before other types, which sort before namespaces — so typing a few letters shows what's
    // actually nearby before it shows something merely imported/visible (TODO.md #51 follow-up: "the
    // in-scope thing first, then a level up").
    private static int ScopeRank(ISymbol symbol) => symbol switch
    {
        ILocalSymbol or IParameterSymbol => 0,
        IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol => 1,
        INamedTypeSymbol => 2,
        INamespaceSymbol => 3,
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

            var pos = position + _preambleLength;
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

            var symInfo = model.GetSymbolInfo(callLike);
            var methods = (symInfo.Symbol is IMethodSymbol s ? [s] : Array.Empty<IMethodSymbol>())
                .Concat(symInfo.CandidateSymbols.OfType<IMethodSymbol>())
                .ToList();

            if (methods.Count == 0 && callLike is InvocationExpressionSyntax { Expression: { } target })
                methods = model.GetMemberGroup(target).OfType<IMethodSymbol>().ToList();

            // Overload resolution came up with nothing usable (e.g. still typing, 0 arguments so far)
            // — fall back to every accessible constructor of the target type, the object-creation
            // equivalent of the GetMemberGroup fallback above.
            if (methods.Count == 0 && callLike is BaseObjectCreationExpressionSyntax objectCreation)
            {
                var createdType = objectCreation is ObjectCreationExpressionSyntax named
                    ? model.GetTypeInfo(named.Type).Type
                    : model.GetTypeInfo(objectCreation).Type; // target-typed `new(...)` — infer from context
                if (createdType is INamedTypeSymbol namedType)
                    methods = namedType.Constructors.Where(c => !c.IsImplicitlyDeclared).ToList();
            }

            if (methods.Count == 0) return null;

            var items = methods.Select(m => (ISignatureHelpItem)new SimpleSignatureHelpItem(m, argIndex)).ToList();
            var selectedIndex = methods.FindIndex(m => m.Parameters.Length >= argCount);
            if (selectedIndex < 0) selectedIndex = methods.Count - 1;

            return new SimpleSignatureHelpResult(items, selectedIndex, argIndex);
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Signature help failed"); return null; }
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

            var pos = position + _preambleLength;
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

    // ── Background analysis (diagnostics + semantic tokens) ───────────────────

    public event Action<string, IReadOnlyList<IDiagnosticInfo>>? DiagnosticsChanged;
    public event Action<string, IReadOnlyList<ISemanticToken>>? SemanticTokensChanged;
    public event Action<string, IReadOnlyList<ICodeFixInfo>>? CodeFixesChanged;

    public void NotifyTextChanged(string code)
    {
        _diagnosticsCts?.Cancel();
        _diagnosticsCts?.Dispose();
        _diagnosticsCts = new CancellationTokenSource();
        var ct = _diagnosticsCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, ct);
                var document = SyncDocument(code);
                await Task.WhenAll(
                    ComputeDiagnosticsAndCodeFixesAsync(code, document, ct),
                    ComputeSemanticTokensAsync(code, document, ct));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Background analysis failed"); }
        }, CancellationToken.None);
    }

    private async Task ComputeSemanticTokensAsync(string code, Document document, CancellationToken ct)
    {
        var textLen = _preambleLength + code.Length;
        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, textLen), ct);

        var result = new List<ISemanticToken>();
        foreach (var span in spans)
        {
            var kind = ClassifySpan(span.ClassificationType);
            if (kind == SemanticTokenKind.None) continue;

            var start = span.TextSpan.Start - _preambleLength;
            if (start < 0) continue;
            var end = Math.Min(span.TextSpan.End - _preambleLength, code.Length);
            if (start >= end) continue;

            result.Add(new SimpleSemanticToken(start, end - start, kind));
        }

        SemanticTokensChanged?.Invoke(code, result);
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

        var visible = new List<IDiagnosticInfo>();
        var forCodeFixes = new List<Diagnostic>();
        foreach (var d in model.GetDiagnostics(cancellationToken: ct))
        {
            if (d.Severity == RoslynSeverity.Hidden) continue;
            forCodeFixes.Add(d);

            if (!d.Location.IsInSource) continue;

            var span = d.Location.SourceSpan;
            var start = span.Start - _preambleLength;
            if (start < 0) continue;

            var end = Math.Max(span.End - _preambleLength, start + 1);
            var severity = d.Severity switch
            {
                RoslynSeverity.Error => ContractSeverity.Error,
                RoslynSeverity.Warning => ContractSeverity.Warning,
                _ => ContractSeverity.Info,
            };
            visible.Add(new SimpleDiagnostic(start, end, d.GetMessage(), severity));
        }

        DiagnosticsChanged?.Invoke(code, visible);

        var fixes = await _codeFixEngine.ComputeFixesAsync(document, forCodeFixes, code, _preambleLength, ct);
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

    public void Dispose()
    {
        _diagnosticsCts?.Cancel();
        _diagnosticsCts?.Dispose();
        _workspace.Dispose();
    }

    // ── Private model types ────────────────────────────────────────────────────

    private sealed record SimpleCompletionItem(
        string DisplayText, CompletionItemKind Kind, IReadOnlyList<DisplayTextSegment> Signature,
        string? Summary, string SortText, double Priority, IReadOnlyList<string> UsingsToAdd) : ICompletionItem;

    private sealed class SimpleCompletionList(IReadOnlyList<ICompletionItem> items) : ICompletionList
    {
        public IReadOnlyList<ICompletionItem> Items => items;
    }

    private sealed class SimpleSignatureHelpResult(
        IReadOnlyList<ISignatureHelpItem> items, int selectedIndex, int argumentIndex) : ISignatureHelpResult
    {
        public IReadOnlyList<ISignatureHelpItem> Items => items;
        public int SelectedItemIndex => selectedIndex;
        public int ArgumentIndex => argumentIndex;
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

    private sealed record SimpleDiagnostic(
        int StartOffset, int EndOffset, string Message,
        ContractSeverity Severity) : IDiagnosticInfo;

    private sealed record SimpleSemanticToken(
        int StartOffset, int Length, SemanticTokenKind Kind) : ISemanticToken;

}
