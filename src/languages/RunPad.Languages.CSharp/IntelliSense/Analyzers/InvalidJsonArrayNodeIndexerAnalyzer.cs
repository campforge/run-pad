using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Sibling of
// InvalidJsonElementPropertyOrIndexAnalyzer (RPAD0364/проходы 368-369) — same "parse the literal JSON at
// analysis time, then check the access against the REAL result" theme, applied to the OTHER JSON API
// surface, `System.Text.Json.Nodes.JsonNode`/`JsonArray` (the mutable DOM), instead of `JsonDocument`/
// `JsonElement` (the read-only one). Deliberately its own analyzer rather than a RPAD0364 extension —
// confirmed empirically that `JsonNode`'s int indexer on an out-of-range array throws
// `ArgumentOutOfRangeException`, a genuinely DIFFERENT exception type from `JsonElement`'s array indexer
// (`IndexOutOfRangeException`, RPAD0364), matching this catalog's established precedent for splitting by
// exception type. `JsonNode.Parse(literal)`/`JsonArray.Parse(literal)` both resolve their static `Parse`
// to the SAME `JsonNode`-declared method (confirmed via symbol resolution, not assumed), and the `[int]`
// indexer likewise always resolves to `JsonNode`'s own — so a single receiver check covers both spellings.
// Deliberately narrower in scope than RPAD0364: only the direct, single-level `JsonNode.Parse(literal)
// [literalIndex]`/`JsonArray.Parse(literal)[literalIndex]` form, receiver PROVABLY a JSON array (its
// SYNTACTIC shape can only be discovered by actually parsing the literal text — reuses `System.Text.Json.
// JsonDocument.Parse` purely as an internal structure-probe for this, same "run the real parser" trick,
// even though the user's own code calls `JsonNode.Parse`/`JsonArray.Parse` instead). NOT extended to
// recursive chains or the string-keyed object indexer this проход — confirmed empirically that `JsonNode`
// 's own STRING-keyed indexer on a missing object key returns `null` rather than throwing (a genuinely
// different, null-propagating design from `JsonElement.GetProperty`'s throwing one), so that indexer form
// doesn't fit this catalog's "always throws" theme at all and is out of scope entirely, not merely
// deferred. Also confirmed (but not chased further, kept out of scope) that an INT index against a
// `JsonObject` node surprisingly does NOT throw either — a further nuance left unexplained and unhandled,
// deliberately narrowing this analyzer to the unambiguous, verified array case only.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidJsonArrayNodeIndexerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0369";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index is out of range for a parsed JsonNode/JsonArray literal",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — the parsed JSON array has only {1} element(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing a JsonNode/JsonArray parsed from a literal JSON array with an index outside its own known element count throws ArgumentOutOfRangeException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ElementAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.Expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" } } parseInvocation)
            return;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;
        if (parseInvocation.ArgumentList.Arguments is not [{ Expression: var jsonArg }, ..]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;
        if (context.SemanticModel.GetConstantValue(jsonArg, ct).Value is not string jsonText) return;

        if (context.SemanticModel.GetSymbolInfo(parseInvocation, ct).Symbol is not IMethodSymbol { Name: "Parse", ContainingType: { } parseType }
            || parseType.ToDisplayString() != "System.Text.Json.Nodes.JsonNode")
        {
            return;
        }
        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true, ContainingType: { } indexerType }
            || indexerType.ToDisplayString() != "System.Text.Json.Nodes.JsonNode")
        {
            return;
        }

        if (TryGetArrayLength(jsonText) is not { } length) return;
        if (index >= 0 && index < length) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), length));
    }

    private static int? TryGetArrayLength(string jsonText)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(jsonText);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
