using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят пятый проход, batch) — own pick, not a Roslynator port.
// `xs.All(x => !pred(x))` reads as a double negative — "every element fails pred" — where
// `!xs.Any(x => pred(x))` ("no element passes pred") says the same thing without stacking two
// negations the reader has to untangle. A pure readability simplification, not a performance one —
// All and Any both short-circuit equally well.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferNotAnyOverAllNegatedPredicateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0093";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer !Any(...) over All(x => !...)",
        messageFormat: "'All(x => !...)' is a double negative — use '!Any(x => ...)' to say the same thing more directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "All(x => !pred(x)) and !Any(x => pred(x)) are equivalent — the second avoids stacking two negations.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "All" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }]) return;

        var body = lambdaExpression switch
        {
            SimpleLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } => exprBody,
            ParenthesizedLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } => exprBody,
            _ => null,
        };
        if (body is not PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression }) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
