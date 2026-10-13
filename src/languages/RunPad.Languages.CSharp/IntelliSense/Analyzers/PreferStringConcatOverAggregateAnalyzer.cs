using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "hand-rolled fold instead of the dedicated method" theme as
// AggregateSumAnalyzer (RPAD0085), for string concatenation specifically —
// AggregateSumAnalyzer is scoped to the numeric SpecialTypes Enumerable.Sum() has overloads for, which
// excludes string entirely, leaving this pattern uncovered. `xs.Aggregate((a, b) => a + b)` over a
// string sequence hand-rolls exactly what `string.Concat(xs)` already does — and does it worse: each
// `+` allocates a new intermediate string, so the fold is O(n^2) in total characters copied, the same
// quadratic-blowup concern ConcatenateStringsInLoopAnalyzer (RPAD0033) already flags for the
// loop-shaped version of this mistake. `xs.Aggregate("", (acc, x) => acc + x)` is the same mistake with
// the seed spelled out explicitly instead of implicitly using the first element — only fires when the
// seed is a compile-time-constant empty string (checked via GetConstantValue), matching
// AggregateSumAnalyzer's own seeded-zero extension.
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

        ArgumentSyntax? seedArg;
        ParenthesizedLambdaExpressionSyntax lambda;
        if (invocation.ArgumentList.Arguments is [{ Expression: ParenthesizedLambdaExpressionSyntax soleLambda }])
        {
            seedArg = null;
            lambda = soleLambda;
        }
        else if (invocation.ArgumentList.Arguments is [var seed, { Expression: ParenthesizedLambdaExpressionSyntax seededLambda }])
        {
            seedArg = seed;
            lambda = seededLambda;
        }
        else
        {
            return;
        }

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
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Aggregate" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (seedArg is null)
        {
            if (method.TypeArguments is not [var sourceType]) return;
            if (sourceType.SpecialType != SpecialType.System_String) return;
        }
        else
        {
            if (method.TypeArguments is not [_, var accumulateType]) return;
            if (accumulateType.SpecialType != SpecialType.System_String) return;
            if (context.SemanticModel.GetConstantValue(seedArg.Expression, ct).Value is not string { Length: 0 })
                return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
