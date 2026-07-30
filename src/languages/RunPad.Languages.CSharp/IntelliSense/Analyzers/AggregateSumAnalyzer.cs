using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят четвёртый проход, batch) — own pick, not a Roslynator port.
// `xs.Aggregate((a, b) => a + b)` hand-rolls exactly what `xs.Sum()` already does, through a generic,
// unoptimized fold — Sum() has a dedicated, type-specific implementation per numeric type. Scoped to
// the numeric types Enumerable.Sum() actually has an overload for (int/long/float/double/decimal) —
// for anything else (e.g. a custom type with its own operator+), Sum() wouldn't even compile, so the
// suggestion would be actively wrong.
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
        if (invocation.ArgumentList.Arguments is not
            [
            {
                Expression: ParenthesizedLambdaExpressionSyntax
                {
                    ParameterList.Parameters: [var p1, var p2],
                    Body: BinaryExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.AddExpression,
                        Left: IdentifierNameSyntax leftId,
                        Right: IdentifierNameSyntax rightId,
                    },
                },
            },
            ])
        {
            return;
        }
        if (leftId.Identifier.Text != p1.Identifier.Text || rightId.Identifier.Text != p2.Identifier.Text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;
        if (method.TypeArguments is not [{ SpecialType: var elementSpecialType }] || !SummableTypes.Contains(elementSpecialType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
