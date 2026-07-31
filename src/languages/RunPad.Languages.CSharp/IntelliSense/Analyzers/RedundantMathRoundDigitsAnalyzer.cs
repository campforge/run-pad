using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят второй проход, batch) — own pick, not a Roslynator port.
// `Math.Round(x, 0)` spells out the digits-of-precision argument that the single-argument
// `Math.Round(x)` overload already defaults to — genuinely redundant, not just a style nit.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantMathRoundDigitsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0124";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Math.Round digits argument",
        messageFormat: "'Math.Round(x, 0)' is the same as 'Math.Round(x)' — the digits parameter already defaults to 0",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Math.Round(x, 0) explicitly passes the digits-of-precision value that the parameterless-precision overload already uses by default.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Round" }) return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var digitsExpression }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Parameters.Length: 2 } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Math") return;

        var constant = context.SemanticModel.GetConstantValue(digitsExpression, ct);
        if (constant.Value is not int digits || digits != 0) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
