using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести тридцатый проход, batch) — own pick, not a Roslynator port.
// `xs.ElementAt(xs.Count() - 1)` spells out "the last element" as an index computed from the total
// count, then walks the sequence to that index — `xs.Last()` says exactly the same thing directly, in
// one pass, without computing an index at all. Matched structurally (the receiver of ElementAt and the
// receiver of Count() must be the literal same expression — SyntaxNode.IsEquivalentTo) plus a semantic
// check that both resolve to the LINQ Enumerable methods, to stay safe for lookalike APIs. Deliberately
// stops at `.Last()` rather than the further `[^1]` step some indexable receivers could also take —
// that step is UseElementAccessInsteadOfLinqMethodAnalyzer's (RPAD0016) territory, and it already
// recognizes a plain `.Last()` call on its own, so this analyzer doesn't need to special-case
// indexable receivers itself.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferLastOverElementAtCountMinusOneAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0218";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Last() over ElementAt(Count() - 1)",
        messageFormat: "'xs.ElementAt(xs.Count() - 1)' can be replaced with 'xs.Last()' — same result, without computing an index first",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ElementAt(Count() - 1) computes an index from the total count and then walks to it — Last() reaches the same element directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ElementAt", Expression: var elementAtReceiver })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            {
                Expression: BinaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SubtractExpression,
                    Left: InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Count", Expression: var countReceiver },
                        ArgumentList.Arguments.Count: 0,
                    } countInvocation,
                    Right: LiteralExpressionSyntax { Token.Value: 1 },
                },
            },
            ])
        {
            return;
        }
        if (!elementAtReceiver.IsEquivalentTo(countReceiver)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ElementAt" } elementAtMethod)
            return;
        if (elementAtMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(countInvocation, ct).Symbol is not IMethodSymbol { Name: "Count" } countMethod)
            return;
        if (countMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
