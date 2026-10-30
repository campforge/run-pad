using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Invoklet.Languages.CSharp.IntelliSense.Syntax;
using Invoklet.Languages.DotnetCommon;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace Invoklet.Languages.CSharp.IntelliSense.Features;

/// <summary>What the editor asks about the symbol under the caret: Quick Info, the occurrences to highlight and rename, and the
/// definitions and implementations to go to. It reads the document the sync hands it and the script context; it changes neither.</summary>
internal sealed class SymbolNavigation(
    Func<string, Microsoft.CodeAnalysis.Document> syncDocument, ScriptDocumentLayout layout, ScriptContext context, ILogger logger)
{
    private readonly Func<string, Microsoft.CodeAnalysis.Document> _syncDocument = syncDocument;
    private readonly ScriptDocumentLayout _layout = layout;
    private readonly ScriptContext _context = context;
    private readonly ILogger _logger = logger;

    // ── Quick Info ─────────────────────────────────────────────────────────────

    internal Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IQuickInfoResult?)await TryGetQuickInfoAsync(code, position, ct), ct);

    private async Task<IQuickInfoResult?> TryGetQuickInfoAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = _syncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = _layout.ToDocumentOffset(code, position);
            if (SourcePositions.IsInsideComment(root, pos) || SourcePositions.IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (token == default) return null;

            var symInfo = model.GetSymbolInfo(token.Parent!);
            var symbol = symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault()
                ?? (ISymbol?)model.GetTypeInfo(token.Parent!).Type;

            if (symbol == null) return null;

            return new SimpleQuickInfoResult(
                symbol.ToDisplayParts(SymbolFormats.Brief).ToSegments(), XmlDocSummary.GetSummary(symbol));
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Quick info failed"); return null; }
    }

    // ── Document Highlights ──────────────────────────────────────────────────────

    // Roslyn ships a ready-made IDocumentHighlightsService for
    // exactly this, but it (and its result types DocumentHighlights/HighlightSpan) are internal to
    // Microsoft.CodeAnalysis.Features — not reachable from here even though that assembly is already
    // loaded into InvokletRoslynHost's MEF composition (confirmed directly: referencing the type by
    // name fails to compile with CS0122, "inaccessible due to its protection level"). Reimplemented
    // with only public Roslyn API instead: find the symbol under the caret, then scan every
    // identifier token in the document for one that resolves to the same symbol.
    internal Task<IReadOnlyList<IDocumentHighlightSpan>?> GetDocumentHighlightsAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IReadOnlyList<IDocumentHighlightSpan>?)await TryGetDocumentHighlightsAsync(code, position, ct), ct);

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

    internal Task<IRenameInfo?> GetRenameInfoAsync(string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IRenameInfo?)await TryGetRenameInfoAsync(code, position, ct), ct);

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
            if (!result.Symbol.Locations.Any(_context.IsInScript)) return null;

            return new SimpleRenameInfo(result.Symbol.Name, result.Spans);
        }
        catch (OperationCanceledException) { throw; } // see the Completions catch above
        catch (Exception ex) { _logger.LogWarning(ex, "Rename info failed"); return null; }
    }

    // ── Go to Definition (F12) ───────────────────────────────────────────────────

    internal Task<IDefinitionResult?> GetDefinitionAsync(string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IDefinitionResult?)await TryGetDefinitionAsync(code, position, ct), ct);

    private async Task<IDefinitionResult?> TryGetDefinitionAsync(string code, int position, CancellationToken ct)
    {
        try
        {
            var document = _syncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = _layout.ToDocumentOffset(code, position);
            if (SourcePositions.IsInsideComment(root, pos) || SourcePositions.IsInsideStringOrCharLiteral(root, pos)) return null;

            var token = root.FindToken(pos);
            if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

            var symbol = ResolveTokenSymbol(model, token);
            if (symbol == null) return null;

            if (symbol.Locations.FirstOrDefault(_context.IsInScript) is { } sourceLocation)
            {
                var offset = _layout.ToUserOffset(code, sourceLocation.SourceSpan.Start);
                if (offset < 0) return null; // declared inside the prepended preamble, not user code
                return new SimpleDefinitionResult(
                    IsInSource: true, Offset: offset,
                    AssemblyPath: null, ContainingTypeFullName: null, MemberName: null,
                    ParameterTypeNames: [], DisplayName: symbol.Name);
            }

            if (_context.GeneratedDocumentFor(symbol) is { } generated)
                return new SimpleDefinitionResult(
                    IsInSource: false, Offset: 0,
                    AssemblyPath: null, ContainingTypeFullName: null, MemberName: null,
                    ParameterTypeNames: [], DisplayName: symbol.ToDisplayString(),
                    GeneratedDocument: generated);

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

    internal Task<IReadOnlyList<IImplementationLocation>?> GetImplementationsAsync(
        string code, int position, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (IReadOnlyList<IImplementationLocation>?)await TryGetImplementationsAsync(code, position, ct), ct);

    private async Task<IReadOnlyList<IImplementationLocation>?> TryGetImplementationsAsync(
        string code, int position, CancellationToken ct)
    {
        try
        {
            var document = _syncDocument(code);
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = _layout.ToDocumentOffset(code, position);
            if (SourcePositions.IsInsideComment(root, pos) || SourcePositions.IsInsideStringOrCharLiteral(root, pos)) return null;

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
                if (impl.Locations.FirstOrDefault(_context.IsInScript) is not { } location) continue;
                var offset = _layout.ToUserOffset(code, location.SourceSpan.Start);
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
        var document = _syncDocument(code);
        var model = await document.GetSemanticModelAsync(ct);
        if (model == null) return null;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return null;

        var pos = _layout.ToDocumentOffset(code, position);
        if (SourcePositions.IsInsideComment(root, pos) || SourcePositions.IsInsideStringOrCharLiteral(root, pos)) return null;

        var token = root.FindToken(pos);
        if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

        var symbol = ResolveTokenSymbol(model, token);
        if (symbol == null) return null;

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

            var userStart = _layout.ToUserOffset(code, candidate.SpanStart);
            if (userStart < 0) continue; // inside the prepended usings preamble, not user code
            spans.Add(new SimpleDocumentHighlightSpan(userStart, candidate.Span.Length));
        }

        return (symbol, spans);
    }

    // Shared by both the caret's own token and every candidate token scanned against it — a
    // declaration site (e.g. the identifier in "var x = ...") has no GetSymbolInfo result of its
    // own (that call is for usages/references), only GetDeclaredSymbol; a usage is the opposite.
    // Trying both, in this order, covers either kind of identifier token uniformly.
    private static ISymbol? ResolveTokenSymbol(SemanticModel model, SyntaxToken token)
    {
        var symbol = model.GetSymbolInfo(token.Parent!).Symbol ?? model.GetDeclaredSymbol(token.Parent!);
        // Calls bind to a reduced extension method (without its receiver parameter); its source
        // declaration is the unreduced method. Both must identify the same rename/highlight symbol.
        return symbol is IMethodSymbol { ReducedFrom: { } method } ? method.OriginalDefinition : symbol;
    }

    private sealed record SimpleQuickInfoResult(
        IReadOnlyList<DisplayTextSegment> Signature, string? Summary) : IQuickInfoResult;

    private sealed record SimpleDocumentHighlightSpan(int Start, int Length) : IDocumentHighlightSpan;

    private sealed record SimpleRenameInfo(
        string CurrentName, IReadOnlyList<IDocumentHighlightSpan> Spans) : IRenameInfo;

    private sealed record SimpleDefinitionResult(
        bool IsInSource, int Offset, string? AssemblyPath, string? ContainingTypeFullName,
        string? MemberName, IReadOnlyList<string> ParameterTypeNames, string DisplayName,
        (string Text, int Line)? GeneratedDocument = null) : IDefinitionResult;

    private sealed record SimpleImplementationLocation(int Offset, string DisplayName) : IImplementationLocation;
}
