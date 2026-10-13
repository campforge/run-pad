using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "combining a value with itself is almost certainly a mistake" theme as SelfSubtractionOrXorAnalyzer
// (RPAD0142, проход 176) and SelfArgumentMathMaxMinAnalyzer (RPAD0225, проход 234), for the remaining
// logical/bitwise operators: `x && x`/`x || x`/`x & x`/`x | x` all evaluate to x's own value unchanged
// (for both bool and integer operands) — a pointless combination almost certainly meant to compare/
// combine x with something else. Deliberately a separate analyzer from RPAD0142 rather than an
// extension — that one's message says the result is always ZERO (true for `-`/`^`), which would be
// WRONG for these operators (the result is x, not necessarily zero), so a shared message wouldn't fit.
// Verified via PROBE that neither the compiler nor any built-in IDE rule already flags these forms
// (unlike the `x = x` self-assignment idea considered alongside this one in the same PROBE round, which
// IS already caught by both CS1717 and a built-in "unnecessary assignment" rule — dropped).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SelfLogicalOrBitwiseOperatorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0226";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Self-combination with &&, ||, & or | always yields the operand unchanged",
        messageFormat: "'x {0} x' always evaluates to x's own value — likely a copy-paste mistake",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "x && x, x || x, x & x, and x | x all evaluate to x unchanged, regardless of its value; this compiles fine but is almost never the intended expression.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.LogicalAndExpression, SyntaxKind.LogicalOrExpression,
            SyntaxKind.BitwiseAndExpression, SyntaxKind.BitwiseOrExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (binary.Left is not IdentifierNameSyntax left) return;
        if (binary.Right is not IdentifierNameSyntax right) return;
        if (left.Identifier.Text != right.Identifier.Text) return;

        var ct = context.CancellationToken;
        var leftSymbol = context.SemanticModel.GetSymbolInfo(left, ct).Symbol;
        var rightSymbol = context.SemanticModel.GetSymbolInfo(right, ct).Symbol;
        if (leftSymbol is null || !SymbolEqualityComparer.Default.Equals(leftSymbol, rightSymbol)) return;

        var operatorText = binary.OperatorToken.Text;
        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operatorText));
    }
}
