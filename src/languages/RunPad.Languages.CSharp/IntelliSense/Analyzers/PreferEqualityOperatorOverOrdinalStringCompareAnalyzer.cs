using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A different method
// than PreferEqualityOperatorOverOrdinalStringEqualsAnalyzer (RPAD0138, проход 179), which covers
// `string.Equals(a, b, StringComparison.Ordinal)`: this is `string.Compare(a, b,
// StringComparison.Ordinal) == 0` (and the other comparison outcomes) — the `==`/`!=` operators on
// strings already compare ordinally, so there's no need to go through the three-way Compare and then
// test its sign. Deliberately scoped to the 3-argument overload with an explicit
// `StringComparison.Ordinal` — the 3-argument `ignoreCase: bool` overload is culture-sensitive, not
// ordinal, and isn't an equivalent target.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEqualityOperatorOverOrdinalStringCompareAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0178";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer == over string.Compare(a, b, StringComparison.Ordinal)",
        messageFormat: "'string.Compare(a, b, StringComparison.Ordinal) {0} 0' can be simplified to 'a {0} b' — the string equality operator already compares ordinally",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The ==/!= operators on strings already perform an ordinal comparison, making string.Compare(a, b, StringComparison.Ordinal) compared against zero redundant.");

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

        var compareInvocation = (binary.Left, binary.Right) switch
        {
            (InvocationExpressionSyntax call, LiteralExpressionSyntax { Token.Value: 0 }) => call,
            _ => null,
        };
        if (compareInvocation is null) return;

        if (compareInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Compare" }) return;
        if (compareInvocation.ArgumentList.Arguments is not [_, _, { Expression: var comparisonArg }]) return;

        var ct = context.CancellationToken;
        if (!IsStringComparisonOrdinal(context.SemanticModel, comparisonArg, ct)) return;

        if (context.SemanticModel.GetSymbolInfo(compareInvocation, ct).Symbol is not IMethodSymbol { Name: "Compare", IsStatic: true } compareMethod)
            return;
        if (compareMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        var operatorText = binary.IsKind(SyntaxKind.EqualsExpression) ? "==" : "!=";
        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operatorText));
    }

    private static bool IsStringComparisonOrdinal(SemanticModel model, ExpressionSyntax expression, CancellationToken ct)
    {
        if (model.GetSymbolInfo(expression, ct).Symbol is not IFieldSymbol { Name: "Ordinal" } field) return false;
        return field.ContainingType?.ToDisplayString() == "System.StringComparison";
    }
}
