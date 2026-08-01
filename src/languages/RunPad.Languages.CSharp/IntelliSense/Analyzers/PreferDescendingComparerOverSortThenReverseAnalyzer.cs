using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести девятый проход, batch) — own pick, not a Roslynator port.
// `xs.Sort(); xs.Reverse();` sorts a List<T> ascending in one pass and then reverses the whole list in
// a second pass to get a descending order — `xs.Sort((a, b) => b.CompareTo(a))` gets the same result
// directly in one pass, with a descending comparer. Scoped to the narrowest safe shape (matching
// PreferListConstructorOverAddRangeAnalyzer, проход 179): a parameterless Sort() call whose VERY NEXT
// sibling statement is a parameterless Reverse() call on that exact same identifier, and nothing else —
// anything in between could depend on the intermediate ascending order.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDescendingComparerOverSortThenReverseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0196";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer a descending comparer over Sort() followed by Reverse()",
        messageFormat: "'Sort()' immediately followed by 'Reverse()' can be combined into 'Sort((a, b) => b.CompareTo(a))' — one pass instead of two",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "List<T>.Sort() followed immediately by Reverse() sorts ascending and then reverses the whole list — Sort((a, b) => b.CompareTo(a)) produces the same descending order directly, in one pass.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ExpressionStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        if (statement.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Sort", Expression: IdentifierNameSyntax sortReceiverId },
                ArgumentList.Arguments.Count: 0,
            } sortInvocation)
        {
            return;
        }

        if (statement.Parent is not BlockSyntax { Statements: var statements }) return;
        var index = statements.IndexOf(statement);
        if (index < 0 || index + 1 >= statements.Count) return;

        if (statements[index + 1] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Reverse", Expression: IdentifierNameSyntax reverseReceiverId },
                    ArgumentList.Arguments.Count: 0,
                } reverseInvocation,
            })
        {
            return;
        }
        if (reverseReceiverId.Identifier.Text != sortReceiverId.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(sortInvocation, ct).Symbol is not IMethodSymbol { Name: "Sort" } sortMethod)
            return;
        if (sortMethod.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
            return;

        if (context.SemanticModel.GetSymbolInfo(reverseInvocation, ct).Symbol is not IMethodSymbol { Name: "Reverse" } reverseMethod)
            return;
        if (reverseMethod.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation()));
    }
}
