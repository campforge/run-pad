using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 159, batch) — own pick, not a Roslynator port. `xs.Select(x =>
// x)` (or `xs.Select((x) => x)`) projects every element onto itself — a no-op wrapped in an extra
// iterator. Deliberately narrow: only the single-parameter, expression-bodied "return the parameter
// unchanged" shape is flagged; anything else (casts, member access, block bodies) is left alone rather
// than guessed at.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantIdentitySelectAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0062";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant identity projection",
        messageFormat: "'Select(x => x)' is a redundant identity projection — remove the Select call and use the sequence directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A Select call whose lambda returns its parameter unchanged does nothing but wrap the sequence in an extra iterator.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }]) return;

        var (parameterName, body) = lambdaExpression switch
        {
            SimpleLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } simple =>
                (simple.Parameter.Identifier.Text, exprBody),
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var soleParam], Body: ExpressionSyntax exprBody } paren =>
                (soleParam.Identifier.Text, exprBody),
            _ => (null, null),
        };
        if (parameterName is null || body is not IdentifierNameSyntax identifier) return;
        if (identifier.Identifier.Text != parameterName) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
