using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Any(x => x == value)` spells out an equality-membership test the same way `xs.Contains(value)`
// already does, more directly — and Contains can dispatch to a specialized, often faster
// implementation for the concrete collection type (e.g. a hash-based lookup) instead of a linear
// element-by-element predicate scan. Matched narrowly: the lambda body must be EXACTLY `param ==
// value` or `value == param` (a bare equality, not wrapped in anything else), and `value` must not
// itself reference the lambda parameter.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferContainsOverAnyEqualityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0082";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Contains over Any with an equality lambda",
        messageFormat: "Use 'Contains(...)' instead of 'Any(x => x == value)' — says the same thing more directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Any(x => x == value) is a manual equality-membership test that Contains(value) already expresses directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Any" })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            {
                Expression: SimpleLambdaExpressionSyntax
                {
                    Body: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } binary,
                } lambda,
            },
            ])
        {
            return;
        }

        var paramName = lambda.Parameter.Identifier.Text;
        var valueExpr = IsParameterReference(binary.Left, paramName) ? binary.Right
            : IsParameterReference(binary.Right, paramName) ? binary.Left
            : null;
        if (valueExpr is null || ReferencesIdentifier(valueExpr, paramName)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsParameterReference(ExpressionSyntax expression, string paramName) =>
        expression is IdentifierNameSyntax { Identifier.Text: var name } && name == paramName;

    private static bool ReferencesIdentifier(ExpressionSyntax expression, string name) =>
        expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.Text == name);
}
