using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 154, batch) — same "always false" family as
// UnsignedValueNeverLessThanZeroAnalyzer (RPAD0007, pass 147), for a different reason: `.Length`/
// `.Count` on string/array/the standard collection interfaces is documented to never be negative, so
// `x.Length < 0` is always false regardless of the type being a plain (signed) int. Deliberately
// restricted to a short, known-safe list of properties (string.Length, array Length, ICollection(<T>)/
// IReadOnlyCollection<T>.Count) rather than any int property literally named Length/Count, to avoid
// guessing at a type's own invariants.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AlwaysFalseNonNegativeLengthComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0051";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Length/Count is never negative",
        messageFormat: "This comparison is always false — '{0}' can never be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.Length, array Length, and the standard collection Count properties are documented to never be negative.");

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
        var candidate = binary.Kind() == SyntaxKind.LessThanExpression ? binary.Left : binary.Right;
        var zeroSide = binary.Kind() == SyntaxKind.LessThanExpression ? binary.Right : binary.Left;

        if (!IsZeroLiteral(zeroSide)) return;
        if (candidate is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Length" or "Count" } memberAccess)
            return;

        if (context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is not IPropertySymbol
            {
                Type.SpecialType: SpecialType.System_Int32,
            } property)
        {
            return;
        }

        if (IsKnownNonNegativeProperty(property))
            context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), memberAccess.Name.Identifier.Text));
    }

    private static bool IsKnownNonNegativeProperty(IPropertySymbol property)
    {
        var containingType = property.ContainingType;
        if (containingType is null) return false;

        if (property.Name == "Length")
        {
            if (containingType.SpecialType is SpecialType.System_String or SpecialType.System_Array) return true;
        }

        if (property.Name == "Count")
        {
            foreach (var type in Enumerate(containingType))
            {
                var name = type.OriginalDefinition.ToDisplayString();
                if (name is "System.Collections.Generic.ICollection<T>"
                    or "System.Collections.Generic.IReadOnlyCollection<T>"
                    or "System.Collections.ICollection")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> Enumerate(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var iface in type.AllInterfaces) yield return iface;
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";
}
