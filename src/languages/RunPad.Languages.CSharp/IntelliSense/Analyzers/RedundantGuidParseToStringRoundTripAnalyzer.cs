using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят четвёртый проход, batch) — own pick, not a Roslynator port. Same
// "redundant round trip" theme as RedundantParseToStringRoundTripAnalyzer (RPAD0097, проход 166), for
// System.Guid specifically — that analyzer is scoped to the numeric SpecialTypes and doesn't cover
// Guid (which has no SpecialType of its own), so this is a separate analyzer rather than an extension.
// `Guid.Parse(g.ToString())`, when `g` is already a Guid, formats it to a string only to immediately
// parse the same string back into an equal Guid — use `g` directly.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantGuidParseToStringRoundTripAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0168";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Guid.Parse(ToString()) round trip",
        messageFormat: "'Guid.Parse(g.ToString())' round-trips through a string when 'g' is already a Guid — use 'g' directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Guid.Parse(g.ToString()), when g is already a Guid, formats g to a string only to immediately parse the same string back into an equal Guid.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax toStringCall }]) return;
        if (toStringCall.ArgumentList.Arguments.Count != 0) return;
        if (toStringCall.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString", Expression: var receiverExpression })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } parseMethod)
            return;
        if (parseMethod.ContainingType?.ToDisplayString() != "System.Guid") return;

        if (context.SemanticModel.GetSymbolInfo(toStringCall, ct).Symbol is not IMethodSymbol { Name: "ToString" }) return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiverExpression, ct).Type;
        if (receiverType?.ToDisplayString() != "System.Guid") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
