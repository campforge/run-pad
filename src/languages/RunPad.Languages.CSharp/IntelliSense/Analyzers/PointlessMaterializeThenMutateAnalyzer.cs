using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "dead code
// via a discarded materialization" theme as PointlessOrderByToListSortAnalyzer (RPAD0113, проход 169),
// generalized: `xs.ToList().Sort();`/`xs.ToList().Reverse();` as a bare statement builds a list,
// mutates it in place, and then throws the whole thing away — the entire statement has no observable
// effect. Deliberately defers to RPAD0113 for the specific `OrderBy(...).ToList().Sort()` shape (that
// one's message calls out the extra "overwrites the custom ordering" angle) to avoid double-messaging
// the exact same statement with two diagnostics.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PointlessMaterializeThenMutateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0123";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Pointless ToList().Sort()/Reverse()",
        messageFormat: "This statement materializes a list and calls '{0}()' on it, then discards the result entirely — likely a bug, since the list is never used",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ToList().Sort()/Reverse() as a bare statement builds a list, mutates it in place, and discards it — the whole statement has no observable effect.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> MutatingMethodNames = ImmutableHashSet.Create("Sort", "Reverse");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ExpressionStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        if (statement.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } mutateInvocation)
            return;
        if (mutateInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var mutateName } mutateAccess)
            return;
        if (!MutatingMethodNames.Contains(mutateName)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(mutateInvocation, ct).Symbol is not IMethodSymbol mutateMethod) return;
        if (mutateMethod.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>") return;

        if (mutateAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } toListInvocation)
            return;
        if (toListInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToList" } toListAccess)
            return;
        if (context.SemanticModel.GetSymbolInfo(toListInvocation, ct).Symbol is not IMethodSymbol toListMethod) return;
        var originalToList = toListMethod.ReducedFrom ?? toListMethod.OriginalDefinition;
        if (originalToList.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (mutateName == "Sort"
            && toListAccess.Expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "OrderBy" or "OrderByDescending" },
            })
        {
            return; // RPAD0113's territory
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation(), mutateName));
    }
}
