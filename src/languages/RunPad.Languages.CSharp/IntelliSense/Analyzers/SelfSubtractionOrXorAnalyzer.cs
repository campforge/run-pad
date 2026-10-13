using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "comparing/combining a value with itself is almost certainly a mistake" theme as
// SelfEqualityCheckAnalyzer (RPAD0096), for two more operators: `x - x` and `x ^ x` are
// both always zero for ANY value of `x` — unlike `x / x`/`x % x` (which throw for `x == 0`), neither
// can throw, so both are unconditionally dead code, not just usually pointless. The compiler's CS1718
// only covers comparison operators (`==`/`!=`/`<`/etc.), not arithmetic/bitwise ones, leaving this gap
// open. Deliberately restricted to a bare identifier on both sides, same as RPAD0096 — two separate
// calls that happen to look identical aren't necessarily the same value if either has side effects.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SelfSubtractionOrXorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0142";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Self-subtraction or self-XOR always yields zero",
        messageFormat: "'x {0} x' always evaluates to zero for any value of 'x' — likely a copy-paste mistake",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "x - x and x ^ x always evaluate to zero regardless of x's value, unlike == or != this isn't caught by the compiler's own CS1718.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SubtractExpression, SyntaxKind.ExclusiveOrExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (binary.Left is not IdentifierNameSyntax leftId || binary.Right is not IdentifierNameSyntax rightId)
            return;
        if (leftId.Identifier.Text != rightId.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (!SymbolEqualityComparer.Default.Equals(
                context.SemanticModel.GetSymbolInfo(leftId, ct).Symbol,
                context.SemanticModel.GetSymbolInfo(rightId, ct).Symbol))
        {
            return;
        }

        var operatorText = binary.IsKind(SyntaxKind.SubtractExpression) ? "-" : "^";
        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operatorText));
    }
}
