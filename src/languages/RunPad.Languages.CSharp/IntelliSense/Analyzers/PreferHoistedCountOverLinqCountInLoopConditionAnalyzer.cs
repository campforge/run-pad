using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `for (int i = 0; i < xs.Count(); i++)` re-evaluates the LINQ `Count()` extension method on every
// iteration — for a plain IEnumerable<T> (no O(1) Count property to fall back on), that's an O(n) walk
// of the whole sequence PER iteration, turning an intended O(n) loop into O(n^2). Hoisting the count
// into a local computed once before the loop fixes both the correctness-adjacent surprise and the
// performance cliff. Deliberately targets the LINQ `Count()` METHOD specifically (verified via symbol,
// not just the name) — a receiver with an O(1) `Count`/`Length` PROPERTY has no such problem and isn't
// this analyzer's concern.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferHoistedCountOverLinqCountInLoopConditionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0145";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "LINQ Count() re-evaluated on every loop iteration",
        messageFormat: "'Count()' in a loop condition walks the whole sequence on every iteration, turning an O(n) loop into O(n^2) — compute it once before the loop instead",
        category: "Performance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A for-loop condition that calls the LINQ Count() extension method re-enumerates the whole sequence on every iteration instead of once, unlike an O(1) Count/Length property.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ForStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var forStatement = (ForStatementSyntax)context.Node;
        if (forStatement.Condition is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LessThanExpression or (int)SyntaxKind.LessThanOrEqualExpression
                    or (int)SyntaxKind.NotEqualsExpression,
                Right: InvocationExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 0,
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" },
                } countInvocation,
            })
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(countInvocation, ct).Symbol is not IMethodSymbol { Name: "Count" } countMethod)
            return;
        if (countMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, forStatement.Condition.GetLocation()));
    }
}
