using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `if (xs.Contains(x)) xs.Remove(x);`/`if (dict.ContainsKey(k)) dict.Remove(k);` looks up the same
// item/key twice — once to check for it, once to remove it. `ICollection<T>.Remove`/
// `Dictionary<TKey,TValue>.Remove(TKey)` are both already safe no-ops that return `false` when the
// item/key isn't present, so the guard adds a redundant lookup without changing behavior.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDirectRemoveOverContainsCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0136";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Contains/ContainsKey check before Remove",
        messageFormat: "Checking '{0}' before calling 'Remove' looks up the same item twice — 'Remove' already returns 'false' safely when it isn't present",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Remove already no-ops and returns false when the item/key isn't present, so a preceding Contains/ContainsKey guard is a redundant second lookup.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.IfStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null) return;

        if (ifStatement.Condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Contains" or "ContainsKey" } conditionAccess,
                ArgumentList.Arguments: [{ Expression: var conditionArg }],
            } conditionInvocation)
        {
            return;
        }

        var body = ifStatement.Statement is BlockSyntax { Statements: [var only] } ? only : ifStatement.Statement;
        if (body is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Remove" } removeAccess,
                    ArgumentList.Arguments: [{ Expression: var removeArg }],
                } removeInvocation,
            })
        {
            return;
        }

        if (!conditionAccess.Expression.IsEquivalentTo(removeAccess.Expression)) return;
        if (!conditionArg.IsEquivalentTo(removeArg)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(conditionInvocation, ct).Symbol is not IMethodSymbol
            { Parameters.Length: 1 } conditionMethod)
        {
            return;
        }
        if (context.SemanticModel.GetSymbolInfo(removeInvocation, ct).Symbol is not IMethodSymbol
            { Name: "Remove", Parameters.Length: 1, ReturnType.SpecialType: SpecialType.System_Boolean })
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.Condition.GetLocation(), conditionMethod.Name));
    }
}
