using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Math.Pow(x, 2)` and `Math.Pow(x, 0.5)` both go through the general floating-point exponentiation
// algorithm for cases that have a much cheaper, exact equivalent: squaring is just one multiplication
// (`x * x`), and a square root already has its own dedicated, more precise function (`Math.Sqrt(x)`).
// Matched via GetConstantValue (not literal text) so `2`/`2.0`/`2d` and `0.5`/`.5` are all recognized
// uniformly.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDirectMathOperationOverPowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0104";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer a direct math operation over Math.Pow for this exponent",
        messageFormat: "'Math.Pow(x, {0})' can be replaced with '{1}' — avoids general floating-point exponentiation for a case that has an exact, cheaper equivalent",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Math.Pow(x, 2)/Math.Pow(x, 0.5) go through general exponentiation for cases that have a cheaper, exact equivalent (x * x / Math.Sqrt(x)).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Pow" }) return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var exponentExpression }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        if (method.ContainingType?.ToDisplayString() != "System.Math") return;

        var constant = context.SemanticModel.GetConstantValue(exponentExpression, ct);
        double? exponentValue = constant.HasValue
            ? constant.Value switch { double d => d, int i => i, float f => f, _ => null }
            : null;
        if (exponentValue is null) return;

        var (exponentText, suggestion) = exponentValue switch
        {
            2 => ("2", "x * x"),
            0.5 => ("0.5", "Math.Sqrt(x)"),
            _ => (null, null),
        };
        if (suggestion is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), exponentText, suggestion));
    }
}
