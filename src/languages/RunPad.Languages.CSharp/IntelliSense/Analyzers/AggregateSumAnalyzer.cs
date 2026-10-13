using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Aggregate((a, b) => a + b)` hand-rolls exactly what `xs.Sum()` already does, through a generic,
// unoptimized fold — Sum() has a dedicated, type-specific implementation per numeric type. Scoped to
// the numeric types Enumerable.Sum() actually has an overload for (int/long/float/double/decimal) —
// for anything else (e.g. a custom type with its own operator+), Sum() wouldn't even compile, so the
// suggestion would be actively wrong.
// Extended (сто шестьдесят шестой проход, batch) to also cover the seeded 2-arg overload —
// `xs.Aggregate(0, (acc, x) => acc + x)` is the same hand-rolled sum, just with the seed spelled out
// explicitly instead of implicitly using the first element. Only fires when the seed is a compile-time
// constant equal to zero (checked via GetConstantValue, not literal text matching, so `0`/`0.0`/`0m`/a
// `const` field all count) — any other seed changes the result, so Sum() wouldn't be equivalent.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateSumAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0085";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Sum instead of Aggregate for addition",
        messageFormat: "Use 'Sum()' instead of 'Aggregate((a, b) => a + b)' — Sum() has a dedicated implementation for this",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Aggregate((a, b) => a + b) hand-rolls a sum via a generic fold — Sum() does the same thing with a type-specific implementation.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> SummableTypes = ImmutableHashSet.Create(
        SpecialType.System_Int32, SpecialType.System_Int64, SpecialType.System_Single,
        SpecialType.System_Double, SpecialType.System_Decimal);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Aggregate" })
            return;

        var lambda = invocation.ArgumentList.Arguments switch
        {
            [{ Expression: ParenthesizedLambdaExpressionSyntax singleArgLambda }] => singleArgLambda,
            [{ Expression: var seedExpression }, { Expression: ParenthesizedLambdaExpressionSyntax seededLambda }]
                when IsZeroConstant(context, seedExpression) => seededLambda,
            _ => null,
        };
        if (lambda is not
            {
                ParameterList.Parameters: [var p1, var p2],
                Body: BinaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.AddExpression,
                    Left: IdentifierNameSyntax leftId,
                    Right: IdentifierNameSyntax rightId,
                },
            })
        {
            return;
        }
        if (leftId.Identifier.Text != p1.Identifier.Text || rightId.Identifier.Text != p2.Identifier.Text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;
        if (method.TypeArguments is not [{ SpecialType: var elementSpecialType }, ..] || !SummableTypes.Contains(elementSpecialType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsZeroConstant(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        var constant = context.SemanticModel.GetConstantValue(expression, context.CancellationToken);
        if (!constant.HasValue) return false;
        try
        {
            return Convert.ToDouble(constant.Value) == 0;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }
}
