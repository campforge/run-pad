using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `list.Insert(list.Count, item)` inserts at the position one past the last element — which is
// exactly what `list.Add(item)` already does, more directly and without the bounds/shift logic
// Insert has to run for the general case. Matched structurally (the receiver on both the Insert call
// and the Count access must be the literal same expression — SyntaxNode.IsEquivalentTo).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferListAddOverInsertAtCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0105";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Add over Insert at Count",
        messageFormat: "'Insert(list.Count, ...)' is exactly what 'Add(...)' already does — use 'Add(...)' directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Insert(list.Count, item) inserts one past the last element — the same thing Add(item) does more directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Insert" } memberAccess)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var indexExpression }, _]) return;
        if (indexExpression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" } countAccess) return;
        if (!countAccess.Expression.IsEquivalentTo(memberAccess.Expression)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            { Name: "Insert", Parameters.Length: 2 } method)
        {
            return;
        }
        if (method.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
