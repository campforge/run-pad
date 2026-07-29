using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок восьмой проход, batch) — idea ported from Roslynator's
// SimplifyBooleanComparisonAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator).
// `flag == true`/`flag != false` (and their negations) say nothing `flag`/`!flag` doesn't already
// say — pure clutter, and a common source of the classic "comparing == true instead of just using the
// value" style bug. Only fires when the non-literal side's static type is exactly `bool` — never for
// a `bool?`, where the comparison is NOT equivalent to the bare value (null-handling differs).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimplifyBooleanComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0020";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Simplify boolean comparison",
        messageFormat: "Comparing a bool to a literal is redundant — use the value directly (negated if needed)",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "'flag == true'/'flag != false' (and their negations) say nothing the bare value doesn't already say.");

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

        var candidate = AsBoolLiteral(binary.Right) is not null ? binary.Left
            : AsBoolLiteral(binary.Left) is not null ? binary.Right : null;
        if (candidate is null) return;
        if (AsBoolLiteral(candidate) is not null) return; // both sides literal — not this analyzer's case

        var type = context.SemanticModel.GetTypeInfo(candidate, context.CancellationToken).Type;
        if (type is { SpecialType: SpecialType.System_Boolean })
            context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool? AsBoolLiteral(ExpressionSyntax expression) => expression.Kind() switch
    {
        SyntaxKind.TrueLiteralExpression => true,
        SyntaxKind.FalseLiteralExpression => false,
        _ => null,
    };
}
