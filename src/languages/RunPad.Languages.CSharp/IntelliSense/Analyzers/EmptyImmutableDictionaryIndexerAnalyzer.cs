using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят шестой проход, batch) — own pick, not a Roslynator port. Same
// "known-empty `.Empty` receiver" theme as EmptyImmutableStackOrQueuePeekOrPopAnalyzer (RPAD0360, same
// проход), applied to the indexer of `ImmutableDictionary<TKey,TValue>`/
// `ImmutableSortedDictionary<TKey,TValue>` instead: `ImmutableDictionary<K,V>.Empty[key]` always throws
// KeyNotFoundException — confirmed empirically via scratch `dotnet run` script — regardless of what
// `key` actually is, since the receiver is PROVABLY empty (unlike DictionaryLiteralMissingKeyAnalyzer/
// RPAD0312, which has to track individual literal keys through a `{ { k, v }, ... }` initializer, this
// needs no such tracking — an empty dictionary has no keys at all, full stop).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyImmutableDictionaryIndexerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0361";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Indexer on ImmutableDictionary<TKey,TValue>.Empty/ImmutableSortedDictionary<TKey,TValue>.Empty always throws",
        messageFormat: "'{0}.Empty[...]' always throws KeyNotFoundException at runtime — the receiver is the known-empty singleton",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing ImmutableDictionary<TKey,TValue>.Empty or ImmutableSortedDictionary<TKey,TValue>.Empty throws KeyNotFoundException regardless of the key, since the receiver provably has no entries.");

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
        if (elementAccess.ArgumentList.Arguments.Count != 1) return;
        if (elementAccess.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Empty" } emptyAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(emptyAccess, ct).Symbol is not IFieldSymbol { Name: "Empty", ContainingType: { } fieldType })
            return;
        var simpleTypeName = fieldType.OriginalDefinition.Name;
        if (simpleTypeName is not ("ImmutableDictionary" or "ImmutableSortedDictionary")) return;
        if (fieldType.OriginalDefinition.ContainingNamespace?.ToDisplayString() != "System.Collections.Immutable")
            return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true, ContainingType: { } indexerType }
            || indexerType.OriginalDefinition.Name != simpleTypeName)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), simpleTypeName));
    }
}
