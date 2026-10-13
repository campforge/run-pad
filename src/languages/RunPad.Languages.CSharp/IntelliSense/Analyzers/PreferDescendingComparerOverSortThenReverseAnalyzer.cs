using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Sort(); xs.Reverse();` sorts a List<T> ascending in one pass and then reverses the whole list in
// a second pass to get a descending order — `xs.Sort((a, b) => b.CompareTo(a))` gets the same result
// directly in one pass, with a descending comparer. Scoped to the narrowest safe shape (matching
// PreferListConstructorOverAddRangeAnalyzer, проход 179): a parameterless Sort() call whose VERY NEXT
// sibling statement is a parameterless Reverse() call on that exact same identifier, and nothing else —
// anything in between could depend on the intermediate ascending order. `Array.Sort(arr);
// Array.Reverse(arr);` is the exact same idea for the static Array methods instead of List<T>'s
// instance methods, so it shares the diagnostic rather than getting its own analyzer.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDescendingComparerOverSortThenReverseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0196";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer a descending comparer over Sort() followed by Reverse()",
        messageFormat: "'{0}' immediately followed by '{1}' can be combined into '{2}' — one pass instead of two",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "List<T>.Sort()/Array.Sort(arr) followed immediately by Reverse() sorts ascending and then reverses the whole sequence — Sort((a, b) => b.CompareTo(a)) produces the same descending order directly, in one pass.");

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
        if (statement.Parent is not BlockSyntax { Statements: var statements }) return;
        var index = statements.IndexOf(statement);
        if (index < 0 || index + 1 >= statements.Count) return;
        if (statements[index + 1] is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax reverseInvocation })
            return;

        var ct = context.CancellationToken;

        // Form 1: xs.Sort(); xs.Reverse(); — List<T> instance methods.
        if (statement.Expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Sort", Expression: IdentifierNameSyntax sortReceiverId },
                ArgumentList.Arguments.Count: 0,
            } listSortInvocation
            && reverseInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Reverse", Expression: IdentifierNameSyntax reverseReceiverId }
            && reverseInvocation.ArgumentList.Arguments.Count == 0
            && reverseReceiverId.Identifier.Text == sortReceiverId.Identifier.Text
            && context.SemanticModel.GetSymbolInfo(listSortInvocation, ct).Symbol is IMethodSymbol { Name: "Sort" } listSortMethod
            && listSortMethod.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>"
            && context.SemanticModel.GetSymbolInfo(reverseInvocation, ct).Symbol is IMethodSymbol { Name: "Reverse" } listReverseMethod
            && listReverseMethod.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation(), "Sort()", "Reverse()", "Sort((a, b) => b.CompareTo(a))"));
            return;
        }

        // Form 2: Array.Sort(arr); Array.Reverse(arr); — static Array methods.
        if (statement.Expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Sort" },
                ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax sortArrayId }],
            } arraySortInvocation
            && reverseInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Reverse" }
            && reverseInvocation.ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax reverseArrayId }]
            && reverseArrayId.Identifier.Text == sortArrayId.Identifier.Text
            && context.SemanticModel.GetSymbolInfo(arraySortInvocation, ct).Symbol is IMethodSymbol { Name: "Sort", IsStatic: true } arraySortMethod
            && arraySortMethod.ContainingType?.ToDisplayString() == "System.Array"
            && context.SemanticModel.GetSymbolInfo(reverseInvocation, ct).Symbol is IMethodSymbol { Name: "Reverse", IsStatic: true } arrayReverseMethod
            && arrayReverseMethod.ContainingType?.ToDisplayString() == "System.Array")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation(), "Array.Sort(arr)", "Array.Reverse(arr)", "Array.Sort(arr, (a, b) => b.CompareTo(a))"));
        }
    }
}
