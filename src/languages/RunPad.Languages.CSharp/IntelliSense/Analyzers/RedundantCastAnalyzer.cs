using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// RemoveRedundantCastAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator). `(T)expr`
// where expr's static type already converts to T via an IDENTITY conversion (Roslyn's own
// SemanticModel.ClassifyConversion — the precise, compiler-grade notion of "no representation change
// at all", not a hand-rolled type-equality guess) changes nothing at runtime.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantCastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0022";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant cast",
        messageFormat: "This cast to '{0}' is redundant — the expression is already that type",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A cast whose target type is an identity conversion from the expression's own type changes nothing at runtime.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.CastExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var cast = (CastExpressionSyntax)context.Node;
        var targetType = context.SemanticModel.GetTypeInfo(cast.Type, context.CancellationToken).Type;
        if (targetType is null) return;

        var conversion = context.SemanticModel.ClassifyConversion(cast.Expression, targetType);
        if (conversion.IsIdentity)
            context.ReportDiagnostic(Diagnostic.Create(Rule, cast.GetLocation(), targetType.ToDisplayString()));
    }
}
