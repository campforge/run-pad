using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 150, batch) — a classic, extremely well-known floating-point
// gotcha (not a Roslynator port — same "real bug, zero false-positive risk" bar as
// SelfReferencingPropertyAnalyzer/RethrowToPreserveStackTraceAnalyzer): per IEEE 754, NaN is never
// equal to anything, including itself — `x == double.NaN` is ALWAYS false and `x != double.NaN` is
// ALWAYS true, no matter what x actually is. `double.IsNaN(x)` is the only correct way to check.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CompareNaNCorrectlyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0037";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Comparing directly to NaN always gives the same result",
        messageFormat: "Comparing to NaN with '==' or '!=' always evaluates the same way regardless of the other operand — use double.IsNaN()/float.IsNaN() instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Per IEEE 754, NaN is never equal to anything, including itself — direct comparison to NaN is always false (or always true for !=).");

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
        if (IsNaNField(context, binary.Left) || IsNaNField(context, binary.Right))
            context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsNaNField(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "NaN" }) return false;

        var symbol = context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol;
        return symbol is IFieldSymbol
        {
            ContainingType.SpecialType: SpecialType.System_Double or SpecialType.System_Single,
        };
    }
}
