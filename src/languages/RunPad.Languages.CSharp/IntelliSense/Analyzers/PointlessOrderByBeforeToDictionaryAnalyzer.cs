using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "ordering thrown away by what comes next" theme as PointlessOrderByToListSortAnalyzer (RPAD0113,
// проход 169), for a different sink: `xs.OrderBy(...).ToDictionary(...)` sorts the sequence only to
// dump it into a Dictionary<TKey, TValue> — a type with no ordering guarantee at all (its enumeration
// order is an implementation detail, not part of its contract). The OrderBy work is pure waste; if the
// caller actually wants the entries visitable in a specific order later, a Dictionary is the wrong
// target type for that regardless.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PointlessOrderByBeforeToDictionaryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0149";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Pointless OrderBy before ToDictionary",
        messageFormat: "Sorting before 'ToDictionary' is pointless — Dictionary<TKey, TValue> doesn't guarantee any enumeration order, so the sort is discarded",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "OrderBy/OrderByDescending immediately before ToDictionary sorts a sequence that's about to be stored in a type with no ordering guarantee — the sort has no observable effect.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToDictionary" } toDictionaryAccess)
            return;

        if (toDictionaryAccess.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "OrderBy" or "OrderByDescending" },
            } orderByInvocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToDictionary" } toDictionaryMethod)
            return;
        if (toDictionaryMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(orderByInvocation, ct).Symbol is not IMethodSymbol { Name: "OrderBy" or "OrderByDescending" } orderByMethod)
            return;
        if (orderByMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
