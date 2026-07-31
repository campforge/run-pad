using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 148, batch) — idea ported from Roslynator's
// UnconstrainedTypeParameterCheckedForNullAnalyzer (RCS1165, Apache License 2.0,
// https://github.com/dotnet/roslynator). `t == null` for an unconstrained generic type parameter T
// compiles (via a boxing-based comparison), but is surprising: if T is instantiated with a value
// type, the boxed value is never null, so the check silently never does what it looks like it does.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnconstrainedTypeParameterCheckedForNullAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0019";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Unconstrained type parameter checked for null",
        messageFormat: "'{0}' has no class/struct constraint — this null check silently does nothing " +
            "if T is instantiated with a value type",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "For an unconstrained type parameter, comparing to null compiles but is misleading for value-type instantiations.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBinary, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
        context.RegisterSyntaxNodeAction(AnalyzeIsPattern, SyntaxKind.IsPatternExpression);
    }

    private static void AnalyzeBinary(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var candidate = binary.Right.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Left
            : binary.Left.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Right : null;
        if (candidate is not null) CheckAndReport(context, candidate);
    }

    private static void AnalyzeIsPattern(SyntaxNodeAnalysisContext context)
    {
        var isPattern = (IsPatternExpressionSyntax)context.Node;
        if (isPattern.Pattern is ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression })
            CheckAndReport(context, isPattern.Expression);
    }

    private static void CheckAndReport(SyntaxNodeAnalysisContext context, ExpressionSyntax candidate)
    {
        var type = context.SemanticModel.GetTypeInfo(candidate, context.CancellationToken).Type;
        if (type is ITypeParameterSymbol
            {
                HasReferenceTypeConstraint: false,
                HasValueTypeConstraint: false,
                HasUnmanagedTypeConstraint: false,
            } typeParam)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, candidate.Parent!.GetLocation(), typeParam.Name));
        }
    }
}
