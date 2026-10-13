using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `!!x`/`!(!x)` negates a boolean expression twice, which is the same as not negating it at all —
// almost always a copy-paste or refactoring leftover rather than an intentional double negation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DoubleNegationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0137";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Double negation",
        messageFormat: "Negating twice cancels out — use the operand directly instead of '!!'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "!!x/!(!x) negates a boolean expression twice, which is equivalent to not negating it at all.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalNotExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var outer = (PrefixUnaryExpressionSyntax)context.Node;

        var operand = outer.Operand;
        while (operand is ParenthesizedExpressionSyntax parenthesized) operand = parenthesized.Expression;

        if (operand is not PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression })
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, outer.GetLocation()));
    }
}
