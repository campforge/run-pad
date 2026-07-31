using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто девяносто пятый проход, batch) — own pick, not a Roslynator port. Same
// "hand-rolled fold instead of the dedicated method" theme as AggregateSumAnalyzer (RPAD0085, проход
// 164), for string concatenation specifically — AggregateSumAnalyzer is scoped to the numeric
// SpecialTypes Enumerable.Sum() has overloads for, which excludes string entirely, leaving this
// pattern uncovered. `xs.Aggregate((a, b) => a + b)` over a string sequence hand-rolls exactly what
// `string.Concat(xs)` already does — and does it worse: each `+` allocates a new intermediate string,
// so the fold is O(n^2) in total characters copied, the same quadratic-blowup concern
// ConcatenateStringsInLoopAnalyzer (RPAD0033) already flags for the loop-shaped version of this
// mistake.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferStringConcatOverAggregateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0183";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use string.Concat instead of Aggregate for string concatenation",
        messageFormat: "Use 'string.Concat(xs)' instead of 'Aggregate((a, b) => a + b)' — Concat builds the result in one pass instead of allocating a new intermediate string per element",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Aggregate((a, b) => a + b) over a string sequence hand-rolls a concatenation fold where each + allocates a new intermediate string; string.Concat(xs) does the same job in one pass.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Aggregate" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: ParenthesizedLambdaExpressionSyntax lambda }])
            return;
        if (lambda.ParameterList.Parameters is not [var p1, var p2]) return;
        if (lambda.Body is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.AddExpression,
                Left: IdentifierNameSyntax leftId,
                Right: IdentifierNameSyntax rightId,
            })
        {
            return;
        }
        if (leftId.Identifier.Text != p1.Identifier.Text || rightId.Identifier.Text != p2.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            { Name: "Aggregate", TypeArguments: [var sourceType] } method)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;
        if (sourceType.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
