using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. `xs.Take(0)`
// always produces an empty sequence regardless of what xs contains — unlike Skip(0) (RPAD0069, a
// harmless no-op), a literal Take(0) almost always signals a bug: a paging/limit computation that was
// supposed to produce a positive count but got left at (or reset to) zero. Warning, not Info, for that
// reason — this is a suspected defect, not a style nit.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SuspiciousTakeZeroAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0070";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Suspicious Take(0)",
        messageFormat: "'Take(0)' always produces an empty sequence — likely unintended",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Take(0) discards every element regardless of the source sequence — usually a leftover or miscalculated limit, not deliberate.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Take" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var argExpr }]) return;
        if (!IsZeroLiteral(argExpr)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";
}
