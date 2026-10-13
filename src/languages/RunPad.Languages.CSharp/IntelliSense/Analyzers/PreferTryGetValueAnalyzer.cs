using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// UseTryGetValueMethodAnalyzer-adjacent family (Apache License 2.0, https://github.com/dotnet/roslynator).
// `dict.ContainsKey(k) ? dict[k] : fallback` looks up the same key TWICE — once to check it exists,
// once to read it — `dict.TryGetValue(k, out var v) ? v : fallback` does both in a single lookup.
// Matched structurally (the receiver and key must appear literally the same on both sides —
// SyntaxNode.IsEquivalentTo, not a guess) to keep false-positive risk near zero, plus a semantic
// check that the receiver's type actually has a TryGetValue method to fall back to.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferTryGetValueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0046";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer TryGetValue over ContainsKey plus indexer",
        messageFormat: "'ContainsKey' plus the indexer looks up the same key twice — use 'TryGetValue' instead for a single lookup",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ContainsKey(k) followed by the indexer [k] performs two separate lookups for the same key; TryGetValue does both in one.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ConditionalExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var conditional = (ConditionalExpressionSyntax)context.Node;
        if (conditional.Condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ContainsKey" } containsKeyAccess,
                ArgumentList.Arguments: [var keyArg],
            } containsKeyInvocation)
        {
            return;
        }

        if (conditional.WhenTrue is not ElementAccessExpressionSyntax { ArgumentList.Arguments: [var indexArg] } elementAccess)
            return;

        if (!elementAccess.Expression.IsEquivalentTo(containsKeyAccess.Expression)) return;
        if (!indexArg.Expression.IsEquivalentTo(keyArg.Expression)) return;

        if (context.SemanticModel.GetSymbolInfo(containsKeyInvocation, context.CancellationToken).Symbol
            is not IMethodSymbol { ContainingType: { } containingType })
        {
            return;
        }
        if (!containingType.GetMembers("TryGetValue").OfType<IMethodSymbol>().Any()) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.GetLocation()));
    }
}
