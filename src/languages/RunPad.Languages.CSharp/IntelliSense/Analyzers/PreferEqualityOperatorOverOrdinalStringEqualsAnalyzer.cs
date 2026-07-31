using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят пятый проход, batch) — own pick, not a Roslynator port.
// The `==`/`!=` operators on `string` already compare ordinally (culture-invariant, case-sensitive) —
// that's their whole point, and exactly what CultureSensitiveStringCompareAnalyzer (RPAD0005, this
// project) pushes people TOWARD when they use something culture-sensitive instead. Spelling that same
// ordinal comparison out as `string.Equals(a, b, StringComparison.Ordinal)` (static) or
// `a.Equals(b, StringComparison.Ordinal)` (instance) is needlessly roundabout — the operator already
// says exactly that.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEqualityOperatorOverOrdinalStringEqualsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0138";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer == over string.Equals(a, b, StringComparison.Ordinal)",
        messageFormat: "'string.Equals(..., StringComparison.Ordinal)' can be simplified to '==' — the string equality operator already compares ordinally",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The == operator on strings already performs an ordinal comparison, making an explicit StringComparison.Ordinal argument redundant.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Equals" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Equals" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        var arguments = invocation.ArgumentList.Arguments;
        ExpressionSyntax? comparisonArg = (method.IsStatic, arguments.Count) switch
        {
            (true, 3) => arguments[2].Expression,
            (false, 2) => arguments[1].Expression,
            _ => null,
        };
        if (comparisonArg is null) return;
        if (!IsStringComparisonOrdinal(context.SemanticModel, comparisonArg, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsStringComparisonOrdinal(SemanticModel model, ExpressionSyntax expression, CancellationToken ct)
    {
        if (model.GetSymbolInfo(expression, ct).Symbol is not IFieldSymbol { Name: "Ordinal" } field) return false;
        return field.ContainingType?.ToDisplayString() == "System.StringComparison";
    }
}
