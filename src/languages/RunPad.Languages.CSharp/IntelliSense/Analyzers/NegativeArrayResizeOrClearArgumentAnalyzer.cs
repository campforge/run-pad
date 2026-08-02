using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести семьдесят первый проход, batch) — own pick, not a Roslynator port. Same
// "literal argument that always throws" theme as NegativeCapacityArgumentAnalyzer/
// NegativeCountArgumentAnalyzer (RPAD0231/RPAD0232): `Array.Resize(ref array, newSize)` throws
// ArgumentOutOfRangeException for a negative `newSize`, and `Array.Clear(array, index, length)` throws
// IndexOutOfRangeException (a DIFFERENT exception type — confirmed empirically before writing this
// rather than assumed) for a negative `index` or `length`. Scoped to a LITERAL negative integer only,
// same reasoning as the rest of this family.
// Extended (двести семьдесят второй проход, batch) to also cover the 5-arg
// `Array.Copy(source, sourceIndex, destination, destinationIndex, length)` overload — throws
// ArgumentOutOfRangeException (confirmed empirically, same as Resize) for a negative `sourceIndex`,
// `destinationIndex`, or `length`. A PROBE snippet with a literal-0 sourceIndex/destinationIndex and a
// negative length also triggers the already-shipped RedundantExplicitFullRangeArrayOperationAnalyzer
// (RPAD0181, "Array.Copy(source, destination, length) would do the same thing") on the same span — a
// different, unrelated concern (that analyzer only checks whether the two start indices are literal 0,
// never looks at the length argument), so both diagnostics coexisting there is expected and fine, same
// precedent as elsewhere in this project.
// Extended again (двести семьдесят третий проход, batch) to also cover the 4-/5-arg
// `Array.BinarySearch(array, index, length, value[, comparer])` overloads — also throws
// ArgumentOutOfRangeException (confirmed empirically) for a negative `index` or `length`.
// Extended again (двести восемьдесят второй проход, batch) to also cover
// `Array.ConstrainedCopy(source, sourceIndex, destination, destinationIndex, length)` — an EXACT
// argument-shape match for the already-covered 5-arg `Array.Copy` (same parameter positions, same
// ArgumentOutOfRangeException), so it reuses that same branch's logic rather than duplicating it.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeArrayResizeOrClearArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0263";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative Array.Resize/Array.Clear/Array.Copy/Array.ConstrainedCopy/Array.BinarySearch argument always throws",
        messageFormat: "'Array.{0}(...)' always throws {1} at runtime — {2} cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Array.Resize, the 5-arg Array.Copy/Array.ConstrainedCopy, and the 4-/5-arg Array.BinarySearch all throw ArgumentOutOfRangeException for a negative index/length argument, and Array.Clear throws IndexOutOfRangeException for a negative index or length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Resize" or "Clear" or "Copy" or "ConstrainedCopy" or "BinarySearch" } access)
            return;
        var methodName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Array") return;

        var arguments = invocation.ArgumentList.Arguments;
        if (methodName == "Resize")
        {
            if (arguments is not [_, { Expression: var newSizeExpr }]) return;
            if (!IsNegativeLiteral(newSizeExpr)) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Resize", "ArgumentOutOfRangeException", "newSize"));
        }
        else if (methodName == "Clear")
        {
            if (arguments is not [_, { Expression: var indexExpr }, { Expression: var lengthExpr }]) return;
            if (IsNegativeLiteral(indexExpr))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Clear", "IndexOutOfRangeException", "index"));
            }
            else if (IsNegativeLiteral(lengthExpr))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Clear", "IndexOutOfRangeException", "length"));
            }
        }
        else if (methodName is "Copy" or "ConstrainedCopy")
        {
            if (arguments is not [_, { Expression: var sourceIndexExpr }, _, { Expression: var destIndexExpr }, { Expression: var copyLengthExpr }])
                return;
            if (IsNegativeLiteral(sourceIndexExpr))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "ArgumentOutOfRangeException", "sourceIndex"));
            }
            else if (IsNegativeLiteral(destIndexExpr))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "ArgumentOutOfRangeException", "destinationIndex"));
            }
            else if (IsNegativeLiteral(copyLengthExpr))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "ArgumentOutOfRangeException", "length"));
            }
        }
        else
        {
            if (arguments.Count is not (4 or 5)) return;
            var searchIndexExpr = arguments[1].Expression;
            var searchLengthExpr = arguments[2].Expression;
            if (IsNegativeLiteral(searchIndexExpr))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "BinarySearch", "ArgumentOutOfRangeException", "index"));
            }
            else if (IsNegativeLiteral(searchLengthExpr))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "BinarySearch", "ArgumentOutOfRangeException", "length"));
            }
        }
    }

    private static bool IsNegativeLiteral(ExpressionSyntax expression) =>
        expression is PrefixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.UnaryMinusExpression,
            Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression },
        };
}
