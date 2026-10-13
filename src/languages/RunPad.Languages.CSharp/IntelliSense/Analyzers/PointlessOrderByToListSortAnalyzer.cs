using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.OrderBy(key).ToList().Sort();` as a bare statement is doubly wrong: the resulting sorted list is
// never captured by anything, so the whole statement is dead code — and even if it HAD been captured,
// List&lt;T&gt;.Sort() with no arguments re-sorts using Comparer&lt;T&gt;.Default, silently throwing away the
// custom ordering OrderBy(key) just established. Scoped to the bare-statement form specifically (the
// result genuinely unused) to keep this a clear, unambiguous case rather than a guess about intent.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PointlessOrderByToListSortAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0113";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Pointless OrderBy().ToList().Sort()",
        messageFormat: "This statement builds a sorted list and immediately discards it — 'Sort()' would also overwrite the OrderBy ordering with the default comparer even if the result were kept",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "OrderBy(key).ToList().Sort() as a bare statement never uses the list it builds, and Sort() with no arguments would discard the OrderBy's custom ordering anyway.");

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
        if (statement.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } sortInvocation)
            return;
        if (sortInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Sort" } sortAccess)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(sortInvocation, ct).Symbol is not IMethodSymbol sortMethod) return;
        if (sortMethod.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>") return;

        if (sortAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } toListInvocation)
            return;
        if (toListInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToList" } toListAccess)
            return;
        if (context.SemanticModel.GetSymbolInfo(toListInvocation, ct).Symbol is not IMethodSymbol toListMethod) return;
        var originalToList = toListMethod.ReducedFrom ?? toListMethod.OriginalDefinition;
        if (originalToList.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (toListAccess.Expression is not InvocationExpressionSyntax orderInvocation) return;
        if (orderInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "OrderBy" or "OrderByDescending" })
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation()));
    }
}
