using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `x.HasValue && x.Value == y` manually spells out exactly what the lifted `==` operator already does
// for `Nullable&lt;T&gt;`: `x == y` checks HasValue first and only then compares the underlying value,
// returning false (not throwing) when x is null. The manual form is longer and repeats `x` for no
// benefit.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferNullableComparisonOverHasValueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0118";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the lifted == operator over HasValue/Value",
        messageFormat: "'x.HasValue && x.Value == y' can be simplified to 'x == y' — the lifted '==' operator already does this check",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "The lifted equality operator on Nullable<T> already checks HasValue before comparing the underlying value — the manual form repeats the receiver for no benefit.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalAndExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (binary.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "HasValue", Expression: IdentifierNameSyntax hasValueId })
            return;
        if (binary.Right is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } valueEquals) return;

        var receiverName = hasValueId.Identifier.Text;
        var valueAccess = ExtractValueAccess(valueEquals.Left, receiverName) ?? ExtractValueAccess(valueEquals.Right, receiverName);
        if (valueAccess is null) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(valueAccess, ct).Symbol is not IPropertySymbol { Name: "Value" } valueProperty) return;
        if (valueProperty.ContainingType?.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T) return;

        if (context.SemanticModel.GetSymbolInfo(binary.Left, ct).Symbol is not IPropertySymbol { Name: "HasValue" } hasValueProperty) return;
        if (hasValueProperty.ContainingType?.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static MemberAccessExpressionSyntax? ExtractValueAccess(ExpressionSyntax expression, string expectedReceiverName) =>
        expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Value", Expression: IdentifierNameSyntax id } access
        && id.Identifier.Text == expectedReceiverName
            ? access
            : null;
}
