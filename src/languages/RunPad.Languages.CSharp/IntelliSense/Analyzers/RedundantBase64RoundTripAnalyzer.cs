using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "redundant round trip" theme as RedundantEncodingRoundTripAnalyzer (RPAD0165):
// `Convert.FromBase64String(Convert.ToBase64String(bytes))` base64-encodes `bytes` only to immediately
// decode that same string right back — the result is exactly `bytes`, and the encode/decode work is
// pure waste.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantBase64RoundTripAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0166";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Convert.FromBase64String(Convert.ToBase64String(...)) round trip",
        messageFormat: "'Convert.FromBase64String(Convert.ToBase64String(bytes))' encodes and immediately decodes 'bytes' — use 'bytes' directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Convert.FromBase64String(Convert.ToBase64String(bytes)) round-trips bytes back to itself through a base64 string — the encode/decode work has no observable effect.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FromBase64String" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax innerInvocation }]) return;

        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToBase64String" }) return;
        if (innerInvocation.ArgumentList.Arguments.Count != 1) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "FromBase64String" } fromMethod)
            return;
        if (fromMethod.ContainingType?.ToDisplayString() != "System.Convert") return;

        if (context.SemanticModel.GetSymbolInfo(innerInvocation, ct).Symbol is not IMethodSymbol { Name: "ToBase64String" } toMethod)
            return;
        if (toMethod.ContainingType?.ToDisplayString() != "System.Convert") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
