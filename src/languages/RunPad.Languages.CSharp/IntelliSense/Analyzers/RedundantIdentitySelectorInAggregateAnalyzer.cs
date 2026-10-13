using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// The aggregate-selector counterpart to
// RedundantIdentitySelectAnalyzer (RPAD0062). `xs.Sum(x => x)` (or Average/Min/Max) passes
// a selector that returns its parameter unchanged — the exact same thing the parameterless overload
// already does, minus the extra delegate invocation per element.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantIdentitySelectorInAggregateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0088";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant identity selector in aggregate call",
        messageFormat: "'{0}(x => x)' is a redundant identity selector — use '{0}()' directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Sum/Average/Min/Max with a selector that returns its parameter unchanged does exactly what the parameterless overload already does.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> AggregateMethodNames = ImmutableHashSet.Create("Sum", "Average", "Min", "Max");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!AggregateMethodNames.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lambdaExpression }]) return;

        var (parameterName, body) = lambdaExpression switch
        {
            SimpleLambdaExpressionSyntax { Body: ExpressionSyntax exprBody } simple =>
                (simple.Parameter.Identifier.Text, exprBody),
            ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var soleParam], Body: ExpressionSyntax exprBody } paren =>
                (soleParam.Identifier.Text, exprBody),
            _ => (null, null),
        };
        if (parameterName is null || body is not IdentifierNameSyntax identifier) return;
        if (identifier.Identifier.Text != parameterName) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
