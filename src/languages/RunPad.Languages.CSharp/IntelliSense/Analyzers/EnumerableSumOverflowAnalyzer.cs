using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок восьмой проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known contents" theme as EmptyArrayLiteralFirstOrSingleOrLastAnalyzer (RPAD0290), but for a
// non-empty case: `Enumerable.Sum()` on a freshly created `int[]`/`long[]` array literal whose own
// literal elements add up to more than the target type's range always throws `OverflowException` at
// runtime — `Sum` uses checked arithmetic internally, unlike a plain `+=` loop, which would silently
// wrap — confirmed empirically via scratch `dotnet run` script, not assumed. Provable here purely by
// summing the literal elements with C#'s own `checked` keyword and catching the same exception type the
// real `Sum()` would throw, rather than reimplementing overflow-detection arithmetic by hand. Scoped to
// the no-selector `.Sum()` overload on a literal `int[]`/`long[]` array whose every element is itself a
// literal (constant) int/long — a selector-based `.Sum(x => ...)` overload, or a non-literal source, is
// not attempted here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EnumerableSumOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0316";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Enumerable.Sum() on a literal array always overflows",
        messageFormat: "'xs.Sum()' always throws OverflowException at runtime — the literal elements add up to more than {0} can represent",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.Sum() throws OverflowException when the checked sum of the source sequence exceeds the element type's range, provable here because the receiver is a freshly created array literal with known, literal elements.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Sum" } access)
            return;
        if (invocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        var elements = TryGetArrayLiteralElements(access.Expression);
        if (elements is null) return;

        var values = new object[elements.Value.Length];
        for (var i = 0; i < elements.Value.Length; i++)
        {
            var constant = context.SemanticModel.GetConstantValue(elements.Value[i], ct);
            if (constant.Value is not (int or long)) return;
            values[i] = constant.Value;
        }
        if (values.Length == 0) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Sum" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var isLong = Array.Exists(values, v => v is long);
        if (!TrySum(values, isLong, out var typeKeyword)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), typeKeyword));
    }

    private static bool TrySum(object[] values, bool isLong, out string typeKeyword)
    {
        typeKeyword = isLong ? "long" : "int";
        try
        {
            checked
            {
                if (isLong)
                {
                    long sum = 0;
                    foreach (var v in values) sum += Convert.ToInt64(v);
                }
                else
                {
                    var sum = 0;
                    foreach (var v in values) sum += (int)v;
                }
            }
            return false;
        }
        catch (OverflowException)
        {
            return true;
        }
    }

    private static ImmutableArray<ExpressionSyntax>? TryGetArrayLiteralElements(ExpressionSyntax expression)
    {
        if (expression is ArrayCreationExpressionSyntax { Initializer: { } explicitInit })
            return [.. explicitInit.Expressions];
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return [.. implicitInit.Expressions];
        if (expression is CollectionExpressionSyntax collectionExpr)
            return [.. collectionExpr.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression)];
        return null;
    }
}
