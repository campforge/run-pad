using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// UnsignedValueNeverLessThanZeroAnalyzer-style rules (Apache License 2.0,
// https://github.com/dotnet/roslynator). `x < 0` (or `0 > x`) is always false when x's static type is
// an unsigned integer — never a genuine runtime check, always a logic error.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsignedValueNeverLessThanZeroAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0007";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Unsigned value is never less than zero",
        messageFormat: "This comparison is always false — an unsigned value can never be less than zero",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Comparing an unsigned integer to a value less than zero always evaluates to false.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LessThanExpression, SyntaxKind.GreaterThanExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        // `x < 0` or `0 > x` — normalize to (unsignedCandidate, zero-check).
        ExpressionSyntax? candidate = binary.Kind() == SyntaxKind.LessThanExpression ? binary.Left : binary.Right;
        var zeroSide = binary.Kind() == SyntaxKind.LessThanExpression ? binary.Right : binary.Left;

        if (zeroSide is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
            || literal.Token.ValueText != "0")
        {
            return;
        }

        var type = context.SemanticModel.GetTypeInfo(candidate, context.CancellationToken).Type;
        if (type is
            {
                SpecialType: SpecialType.System_Byte or SpecialType.System_UInt16
            or SpecialType.System_UInt32 or SpecialType.System_UInt64
            })
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
        }
    }
}
