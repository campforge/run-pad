using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят третий проход, batch) — own pick, not a Roslynator port.
// `!(x == y)`/`!(x != y)` negates an equality comparison instead of just using its opposite operator
// directly. Deliberately scoped to == and != only, not the relational operators (</>/<=/>=) —
// negating THOSE isn't a safe rewrite for floating-point types, since every comparison against NaN
// returns false, making `!(x < y)` true for NaN while `x >= y` is false for it. == and != don't have
// that asymmetry: `!(x == y)` and `x != y` agree for every input, NaN included.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferNotEqualsOverNegatedEqualsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0128";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the direct operator over negating an equality comparison",
        messageFormat: "Negating an equality comparison can be simplified using '{0}' directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "!(x == y) and !(x != y) negate an equality comparison instead of using its opposite operator directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalNotExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var unary = (PrefixUnaryExpressionSyntax)context.Node;
        if (unary.Operand is not ParenthesizedExpressionSyntax { Expression: BinaryExpressionSyntax binary })
            return;

        var suggestion = binary.Kind() switch
        {
            SyntaxKind.EqualsExpression => "!=",
            SyntaxKind.NotEqualsExpression => "==",
            _ => null,
        };
        if (suggestion is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, unary.GetLocation(), suggestion));
    }
}
