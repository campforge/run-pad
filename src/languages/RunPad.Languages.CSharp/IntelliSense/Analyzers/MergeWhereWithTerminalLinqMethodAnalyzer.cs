using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same LINQ-hygiene theme as
// PreferMinByMaxByOverOrderByFirstAnalyzer next to this one. `xs.Where(pred).First()` (or
// FirstOrDefault/Single/SingleOrDefault/Any/Last/LastOrDefault) builds an intermediate filtered
// sequence just to immediately consume it — every one of these methods has its own predicate-accepting
// overload (`xs.First(pred)`) that does the same filtering in a single pass, without the extra
// iterator.
// Extended (pass 159, batch) to also cover Count — but only when the Where().Count()
// invocation ISN'T itself being compared to zero, since CallAnyInsteadOfCountAnalyzer (RPAD0017)
// already flags that exact shape with a strictly better suggestion (Any(pred) beats Count(pred) > 0)
// — reporting both here would double-message the same span.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MergeWhereWithTerminalLinqMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0054";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Merge Where with the following LINQ method",
        messageFormat: "'Where(...).{0}()' can be simplified to '{0}(...)' — passing the predicate directly avoids the intermediate filtered sequence",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Where(predicate) followed immediately by a predicate-accepting terminal LINQ method builds an unnecessary intermediate sequence.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TerminalMethodNames = ImmutableHashSet.Create(
        "First", "FirstOrDefault", "Single", "SingleOrDefault", "Any", "Last", "LastOrDefault", "Count");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var terminalName } memberAccess)
            return;
        if (!TerminalMethodNames.Contains(terminalName)) return;
        if (terminalName == "Count" && IsCompareToZero(invocation)) return; // defer to RPAD0017

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } whereInvocation)
            return;
        if (whereInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Where" }) return;
        if (!IsRealEnumerableMethod(context, whereInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), terminalName));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }

    private static bool IsCompareToZero(InvocationExpressionSyntax invocation)
    {
        if (invocation.Parent is not BinaryExpressionSyntax binary) return false;
        var other = binary.Left == invocation ? binary.Right : binary.Left;
        return other is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
            && literal.Token.ValueText == "0";
    }
}
