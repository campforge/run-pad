using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста девятый проход, batch) — own pick, not a Roslynator port. Same "negative index/
// length always throws" theme as NegativeArrayResizeOrClearArgumentAnalyzer (RPAD0263), applied to
// `List<T>`'s own (index, count)-taking instance methods instead of `Array`'s static ones:
// `list.Sort(index, count, comparer)`/`list.Reverse(index, count)`/`list.GetRange(index, count)`/
// `list.RemoveRange(index, count)` all throw ArgumentOutOfRangeException for a negative `index` or
// `count` — confirmed empirically via scratch `dotnet run` script, not assumed. Unlike Array's static
// methods (where the array is argument 0), these are instance methods, so `index`/`count` are arguments
// 0 and 1, not 1 and 2. Scoped to a LITERAL negative integer only, same reasoning as the rest of this
// family — `Sort`'s 0-/1-argument overloads (no index/count) and `Reverse()`'s 0-argument overload are
// naturally excluded by the argument-count check.
// Extended in the триста десятый проход to also cover `list.BinarySearch(index, count, item, comparer)`
// (same shape as `Sort`) and `list.CopyTo(index, array, arrayIndex, count)` — confirmed empirically that
// all three of `CopyTo`'s numeric arguments (`index`, `arrayIndex`, AND `count`) independently throw
// ArgumentOutOfRangeException when negative, unlike the others' two-argument shape.
// Extended again in the триста тридцать первый проход to also cover `list.IndexOf(item, index[, count])`
// and `list.LastIndexOf(item, index[, count])` — confirmed empirically to throw for a negative `index`
// OR `count` exactly like the rest of this family (this analyzer, unlike its Array-family cousins
// InvalidArrayFillArgumentAnalyzer/InvalidArrayLastIndexOfArgumentAnalyzer, RPAD0294/RPAD0298, never
// checks the upper-bound "index + count exceeds length" case at all — it only proves negativity, which
// needs no knowledge of the list's length or even a literal-length list — so this extension is exactly
// as narrow in scope as the rest of RPAD0287 already is, not a new capability). `item` sits at position
// 0, shifting `index`/`count` to positions 1/2 — handled via a new per-method-name branch, same pattern
// as the existing `CopyTo` branch. Both the 2-argument (`item, index`) and 3-argument (`item, index,
// count`) overloads are covered; the 1-argument `IndexOf(item)`/`LastIndexOf(item)` overload has no
// index/count to check and is naturally excluded by the argument-count check.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeListIndexOrCountArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0287";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative List<T> index/count argument always throws",
        messageFormat: "'xs.{0}(...)' always throws ArgumentOutOfRangeException at runtime — {1} cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "List<T>.Sort(index, count, comparer), Reverse(index, count), GetRange(index, count), RemoveRange(index, count), BinarySearch(index, count, item, comparer), CopyTo(index, array, arrayIndex, count), IndexOf(item, index, count), and LastIndexOf(item, index, count) all throw ArgumentOutOfRangeException for a negative index/count/arrayIndex.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Sort" or "Reverse" or "GetRange" or "RemoveRange" or "BinarySearch" or "CopyTo" or "IndexOf" or "LastIndexOf" } access)
            return;
        var methodName = access.Name.Identifier.Text;

        var arguments = invocation.ArgumentList.Arguments;
        var validArgCount = methodName switch
        {
            "Sort" => arguments.Count == 3,
            "BinarySearch" or "CopyTo" => arguments.Count == 4,
            "IndexOf" or "LastIndexOf" => arguments.Count is 2 or 3,
            _ => arguments.Count == 2,
        };
        if (!validArgCount) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType is not { IsGenericType: true } containingType
            || containingType.ConstructedFrom.ToDisplayString() != "System.Collections.Generic.List<T>")
        {
            return;
        }

        if (methodName == "CopyTo")
        {
            // CopyTo(index, array, arrayIndex, count) — three independently-checked numeric arguments.
            if (IsNegativeLiteral(arguments[0].Expression))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "index"));
            }
            else if (IsNegativeLiteral(arguments[2].Expression))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "arrayIndex"));
            }
            else if (IsNegativeLiteral(arguments[3].Expression))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "count"));
            }
            return;
        }

        if (methodName is "IndexOf" or "LastIndexOf")
        {
            // IndexOf(item, index[, count])/LastIndexOf(item, index[, count]) — item sits at position 0.
            if (IsNegativeLiteral(arguments[1].Expression))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "index"));
            }
            else if (arguments.Count == 3 && IsNegativeLiteral(arguments[2].Expression))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "count"));
            }
            return;
        }

        var indexExpr = arguments[0].Expression;
        var countExpr = arguments[1].Expression;
        if (IsNegativeLiteral(indexExpr))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "index"));
        }
        else if (IsNegativeLiteral(countExpr))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "count"));
        }
    }

    private static bool IsNegativeLiteral(ExpressionSyntax expression) =>
        expression is PrefixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.UnaryMinusExpression,
            Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression },
        };
}
