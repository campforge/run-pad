using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок пятый проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known contents" theme as ArrayLiteralIndexerOutOfRangeAnalyzer/ListLiteralIndexerOutOfRangeAnalyzer
// (RPAD0283/RPAD0303), applied to `Dictionary<TKey,TValue>` instead of an array/list: indexing a freshly
// created `new Dictionary<K, V> { { k1, v1 }, { k2, v2 } }` with a literal key that matches NONE of the
// initializer's own literal keys always throws `KeyNotFoundException` — confirmed empirically via
// scratch `dotnet run` script, not assumed. Scoped to the classic `{ { key, value }, ... }`
// complex-element-initializer syntax only (not the newer `[key] = value` indexer-initializer form, a
// different syntax shape not attempted here), and only proceeds when EVERY key in the initializer is
// itself a compile-time constant of the same type as the indexer argument — if even one key can't be
// resolved as a constant, the analyzer bails out entirely rather than risk a false positive from an
// incomplete picture of the dictionary's actual contents.
// Extended in the триста семьдесят первый проход to also cover `SortedList<TKey,TValue>`/
// `SortedDictionary<TKey,TValue>` — confirmed empirically to throw the exact same `KeyNotFoundException`
// for a missing literal key as `Dictionary<TKey,TValue>` does, same mechanism, so an extension rather
// than a new analyzer (matching e.g. RPAD0290's own multi-type extension precedent). The type check was
// widened from an exact `OriginalDefinition.ToDisplayString()` string match to a namespace+name+arity
// check instead, to avoid depending on the EXACT type-parameter-name spelling embedded in that display
// string (`Dictionary<TKey, TValue>` happens to already match the real parameter names, but that's an
// implementation-detail coincidence not worth relying on for two more types).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DictionaryLiteralMissingKeyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0312";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Indexer key is missing from a Dictionary<TKey,TValue>/SortedList<TKey,TValue>/SortedDictionary<TKey,TValue> literal",
        messageFormat: "Indexing this {0}<TKey,TValue> literal with this key always throws KeyNotFoundException at runtime — the key is not present in the initializer",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing a freshly created Dictionary<TKey,TValue>/SortedList<TKey,TValue>/SortedDictionary<TKey,TValue> collection initializer with a literal key that matches none of the initializer's own literal keys throws KeyNotFoundException.");

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
        InitializerExpressionSyntax? initializer = elementAccess.Expression switch
        {
            ObjectCreationExpressionSyntax { Initializer: { } explicitInit } => explicitInit,
            ImplicitObjectCreationExpressionSyntax { Initializer: { } implicitInit } => implicitInit,
            _ => null,
        };
        if (initializer is null || initializer.Kind() != SyntaxKind.CollectionInitializerExpression) return;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var keyArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetTypeInfo(elementAccess.Expression, ct).Type?.OriginalDefinition is not INamedTypeSymbol original)
            return;
        if (original.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic"
            || original.Arity != 2
            || original.Name is not ("Dictionary" or "SortedList" or "SortedDictionary"))
        {
            return;
        }

        var keyValue = context.SemanticModel.GetConstantValue(keyArg, ct);
        if (!keyValue.HasValue) return;

        var entryKeys = new object?[initializer.Expressions.Count];
        for (var i = 0; i < initializer.Expressions.Count; i++)
        {
            if (initializer.Expressions[i] is not InitializerExpressionSyntax { Expressions: [var entryKeyExpr, _] }) return;
            var entryKeyValue = context.SemanticModel.GetConstantValue(entryKeyExpr, ct);
            if (!entryKeyValue.HasValue) return;
            entryKeys[i] = entryKeyValue.Value;
        }

        if (entryKeys.Length == 0) return;
        if (entryKeys.Any(k => Equals(k, keyValue.Value))) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), original.Name));
    }
}
