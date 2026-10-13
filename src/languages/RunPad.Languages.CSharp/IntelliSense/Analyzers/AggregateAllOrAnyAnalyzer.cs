using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "hand-rolled fold instead of the dedicated method" theme as AggregateSumAnalyzer (RPAD0085, проход
// 164) and AggregateMaxMinAnalyzer (RPAD0204, проход 219): `xs.Aggregate(true, (acc, x) => acc &&
// predicate(x))` hand-rolls exactly what `xs.All(predicate)` already does, and the false/|| mirror
// hand-rolls `xs.Any(predicate)`. Unlike the seeded Max/Min case considered and rejected in проход
// 219's brainstorming (Aggregate on an empty sequence returns the seed silently, while Max()/Min()
// throw — a real behavioral gap), All()/Any() do NOT throw on an empty sequence: both simply return
// their identity (true for All, false for Any) for an empty source, exactly matching what
// Aggregate(seed, ...) already returns for an empty source with that same seed — no behavioral gap
// here, so this one is safe where the Max/Min seeded case wasn't.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateAllOrAnyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0214";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use All/Any instead of Aggregate for a boolean fold",
        messageFormat: "Use '{0}(predicate)' instead of hand-rolling the same fold with 'Aggregate' — {0}() has a dedicated implementation for this",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Aggregate(true, (acc, x) => acc && predicate(x)) / Aggregate(false, (acc, x) => acc || predicate(x)) hand-roll exactly what All(predicate)/Any(predicate) already do.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Aggregate" })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            { Expression: var seedExpr },
            {
                Expression: ParenthesizedLambdaExpressionSyntax
                {
                    ParameterList.Parameters: [{ Identifier.Text: var accParamName }, _],
                    Body: BinaryExpressionSyntax { Left: IdentifierNameSyntax leftId } bodyBinary,
                },
            },
            ])
        {
            return;
        }
        if (leftId.Identifier.Text != accParamName) return;

        var ct = context.CancellationToken;
        var seedValue = context.SemanticModel.GetConstantValue(seedExpr, ct).Value;
        string linqMethodName;
        if (seedValue is true && bodyBinary.IsKind(SyntaxKind.LogicalAndExpression))
        {
            linqMethodName = "All";
        }
        else if (seedValue is false && bodyBinary.IsKind(SyntaxKind.LogicalOrExpression))
        {
            linqMethodName = "Any";
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Aggregate" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), linqMethodName));
    }
}
