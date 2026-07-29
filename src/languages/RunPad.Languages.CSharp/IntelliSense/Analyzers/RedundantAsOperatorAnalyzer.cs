using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок восьмой проход, batch) — same idea as RedundantCastAnalyzer, for `as`
// instead of a C-style cast (Roslynator's own catalog treats these as separate rules too, since `as`
// has different failure semantics — null instead of an exception — worth its own diagnostic even
// though the "redundant" detection itself is identical).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantAsOperatorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0023";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant 'as' operator",
        messageFormat: "This 'as {0}' is redundant — the expression is already that type",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An 'as T' whose target type is an identity conversion from the expression's own type changes nothing at runtime.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.AsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var asExpression = (BinaryExpressionSyntax)context.Node;
        var targetType = context.SemanticModel.GetTypeInfo(asExpression.Right, context.CancellationToken).Type;
        if (targetType is null) return;

        var conversion = context.SemanticModel.ClassifyConversion(asExpression.Left, targetType);
        if (conversion.IsIdentity)
            context.ReportDiagnostic(Diagnostic.Create(Rule, asExpression.GetLocation(), targetType.ToDisplayString()));
    }
}
