using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// The "write" counterpart to PreferTryGetValueAnalyzer
// (RPAD0046, pass 152, which covers the "read" shape: ContainsKey + indexer get). `if
// (!dict.ContainsKey(k)) dict[k] = v;` looks up the same key twice — once to check absence, once to
// insert — `dict.TryAdd(k, v)` does both in a single lookup. Matched structurally (receiver and key
// must appear literally the same — SyntaxNode.IsEquivalentTo) plus a semantic check that the
// receiver's type actually has a TryAdd method, same discipline as RPAD0046.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferTryAddOverContainsKeyCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0066";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer TryAdd over a negated ContainsKey check plus the indexer",
        messageFormat: "'!ContainsKey' plus the indexer set looks up the same key twice — use 'TryAdd' instead for a single lookup",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "!ContainsKey(k) followed by the indexer set [k] = v performs two separate lookups for the same key; TryAdd does both in one.");

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

        if (ifStatement.Condition is not PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } negation)
            return;
        if (negation.Operand is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ContainsKey" } containsKeyAccess,
                ArgumentList.Arguments: [var keyArg],
            } containsKeyInvocation)
        {
            return;
        }

        var innerStatement = UnwrapSingleStatement(ifStatement.Statement);
        if (innerStatement is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                    Left: ElementAccessExpressionSyntax { ArgumentList.Arguments: [var indexArg] } elementAccess,
                },
            })
        {
            return;
        }

        if (!elementAccess.Expression.IsEquivalentTo(containsKeyAccess.Expression)) return;
        if (!indexArg.Expression.IsEquivalentTo(keyArg.Expression)) return;

        if (context.SemanticModel.GetSymbolInfo(containsKeyInvocation, context.CancellationToken).Symbol
            is not IMethodSymbol { ContainingType: { } containingType })
        {
            return;
        }
        if (!containingType.GetMembers("TryAdd").OfType<IMethodSymbol>().Any()) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.GetLocation()));
    }

    private static StatementSyntax UnwrapSingleStatement(StatementSyntax statement) =>
        statement is BlockSyntax { Statements: [var single] } ? single : statement;
}
