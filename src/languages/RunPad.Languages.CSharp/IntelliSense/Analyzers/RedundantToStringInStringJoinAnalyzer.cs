using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят пятый проход, batch) — own pick, not a Roslynator port.
// `string.Join(sep, xs.Select(x => x.ToString()))` projects every element to a string just to hand it
// to Join — but string.Join already calls ToString() on each element internally (that's how the
// generic `Join<T>(string, IEnumerable<T>)` overload works for non-string T). The Select is a
// redundant extra pass building a throwaway intermediate sequence of strings.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantToStringInStringJoinAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0091";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant ToString() before string.Join",
        messageFormat: "'string.Join' already calls ToString() on each element — pass the sequence directly instead of 'Select(x => x.ToString())'",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "string.Join(sep, xs.Select(x => x.ToString())) redundantly projects to strings first — string.Join already calls ToString() on each element.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Join" }) return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: InvocationExpressionSyntax selectInvocation }])
            return;
        if (selectInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" }) return;
        if (selectInvocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }]) return;

        var (paramName, body) = lambdaExpression switch
        {
            SimpleLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } simple =>
                (simple.Parameter.Identifier.Text, exprBody),
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var soleParam], Body: ExpressionSyntax exprBody } paren =>
                (soleParam.Identifier.Text, exprBody),
            _ => (null, null),
        };
        if (paramName is null) return;
        if (body is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: "ToString",
                    Expression: IdentifierNameSyntax { Identifier.Text: var receiverName },
                },
            })
        {
            return;
        }
        if (receiverName != paramName) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol joinMethod) return;
        if (joinMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        if (context.SemanticModel.GetSymbolInfo(selectInvocation, ct).Symbol is not IMethodSymbol selectMethod) return;
        var original = selectMethod.ReducedFrom ?? selectMethod.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
