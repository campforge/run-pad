using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `Math.Abs(x) == 0`/`!= 0` calls into Math.Abs just to compare the result against zero — but a value's
// absolute value is zero if and only if the value itself is zero, so `x == 0`/`x != 0` says exactly the
// same thing without the extra call (and without Math.Abs's own overflow risk at MinValue for signed
// integer types).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantMathAbsInZeroComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0150";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Math.Abs in a zero comparison",
        messageFormat: "'Math.Abs(x) {0} 0' can be simplified to 'x {0} 0' — a value's absolute value is zero exactly when the value itself is",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Math.Abs(x) == 0/!= 0 is equivalent to comparing x itself to zero, without the extra call or its overflow risk at MinValue for signed integer types.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        var (absCall, zeroLiteral) = (binary.Left, binary.Right) switch
        {
            (InvocationExpressionSyntax call, LiteralExpressionSyntax literal) when IsZeroLiteral(literal) => (call, literal),
            (LiteralExpressionSyntax literal, InvocationExpressionSyntax call) when IsZeroLiteral(literal) => (call, literal),
            _ => (null, null),
        };
        if (absCall is null || zeroLiteral is null) return;

        if (absCall.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Abs" }) return;
        if (absCall.ArgumentList.Arguments.Count != 1) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(absCall, ct).Symbol is not IMethodSymbol { Name: "Abs" } absMethod)
            return;
        if (absMethod.ContainingType?.ToDisplayString() != "System.Math") return;

        var operatorText = binary.IsKind(SyntaxKind.EqualsExpression) ? "==" : "!=";
        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operatorText));
    }

    private static bool IsZeroLiteral(LiteralExpressionSyntax literal) =>
        literal.Token.Value is 0 or 0L or 0.0 or 0.0f or 0m;
}
