using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.IndexOf(x) >= 0` (or `!= -1`/`> -1`) and `xs.IndexOf(x) == -1` (or `< 0`) are membership checks
// spelled out in terms of a position that's then thrown away — `xs.Contains(x)` says exactly the same
// thing more directly, and for types with a specialized Contains (e.g. HashSet<T>'s O(1) lookup vs
// IndexOf's O(n) scan) can be meaningfully faster too. Matched against string.IndexOf/List<T>.IndexOf
// and any other type with a real instance IndexOf(T) plus a matching Contains(T) — verified
// semantically, not by receiver type name, so custom collections with the same shape are covered too.
// Extended (сто шестьдесят девятый проход, batch) to also cover the STATIC `Array.IndexOf(array,
// value)` form — arrays don't have a callable public instance IndexOf, so that idiom uses the static
// method instead, but the same "compared to -1/0 just to check membership" shape applies.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferContainsOverIndexOfComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0102";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Contains over comparing IndexOf to -1/0",
        messageFormat: "Comparing 'IndexOf(...)' to -1/0 can be simplified using 'Contains(...)' — says the same thing more directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "IndexOf(x) compared against -1 or 0 is a membership check spelled out via a position that's immediately discarded — Contains(x) says the same thing directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.GreaterThanOrEqualExpression, SyntaxKind.GreaterThanExpression,
            SyntaxKind.LessThanExpression, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        var indexOfSide = binary.Kind() switch
        {
            SyntaxKind.GreaterThanOrEqualExpression when IsZeroLiteral(binary.Right) => binary.Left, // IndexOf(x) >= 0
            SyntaxKind.GreaterThanExpression when IsNegativeOneLiteral(binary.Right) => binary.Left, // IndexOf(x) > -1
            SyntaxKind.LessThanExpression when IsZeroLiteral(binary.Right) => binary.Left, // IndexOf(x) < 0
            SyntaxKind.NotEqualsExpression when IsNegativeOneLiteral(binary.Right) => binary.Left, // IndexOf(x) != -1
            SyntaxKind.NotEqualsExpression when IsNegativeOneLiteral(binary.Left) => binary.Right, // -1 != IndexOf(x)
            SyntaxKind.EqualsExpression when IsNegativeOneLiteral(binary.Right) => binary.Left, // IndexOf(x) == -1
            SyntaxKind.EqualsExpression when IsNegativeOneLiteral(binary.Left) => binary.Right, // -1 == IndexOf(x)
            _ => null,
        };
        if (indexOfSide is not InvocationExpressionSyntax indexOfInvocation) return;

        var ct = context.CancellationToken;
        var receiverExpression = GetContainsReceiver(context, indexOfInvocation, ct);
        if (receiverExpression is null) return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiverExpression, ct).Type;
        if (receiverType is null || !HasContainsMethod(receiverType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static ExpressionSyntax? GetContainsReceiver(
        SyntaxNodeAnalysisContext context, InvocationExpressionSyntax indexOfInvocation, CancellationToken ct)
    {
        // Instance form: xs.IndexOf(value)
        if (indexOfInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "IndexOf" } indexOfAccess
            && indexOfInvocation.ArgumentList.Arguments.Count == 1
            && context.SemanticModel.GetSymbolInfo(indexOfInvocation, ct).Symbol is IMethodSymbol { Name: "IndexOf", Parameters.Length: 1, IsStatic: false })
        {
            return indexOfAccess.Expression;
        }

        // Static form: Array.IndexOf(array, value) — arrays have no callable public instance IndexOf.
        if (indexOfInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "IndexOf" }
            && indexOfInvocation.ArgumentList.Arguments is [{ Expression: var arrayExpression }, _]
            && context.SemanticModel.GetSymbolInfo(indexOfInvocation, ct).Symbol is IMethodSymbol
            {
                Name: "IndexOf", IsStatic: true, ContainingType.SpecialType: SpecialType.System_Array,
            })
        {
            return arrayExpression;
        }

        return null;
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";

    private static bool IsNegativeOneLiteral(ExpressionSyntax expression) =>
        expression is PrefixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.UnaryMinusExpression,
            Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
        } && literal.Token.ValueText == "1";

    private static bool HasContainsMethod(ITypeSymbol type)
    {
        foreach (var candidate in EnumerateSelfAndInterfaces(type))
        {
            if (candidate.GetMembers("Contains").OfType<IMethodSymbol>().Any(m => m.Parameters.Length == 1))
                return true;
        }
        return false;
    }

    private static IEnumerable<ITypeSymbol> EnumerateSelfAndInterfaces(ITypeSymbol type)
    {
        yield return type;
        foreach (var iface in type.AllInterfaces) yield return iface;
    }
}
