using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят третий проход, batch) — own pick, not a Roslynator port.
// `Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s))` encodes `s` to bytes only to immediately decode
// those same bytes back with the SAME encoding — the round trip returns exactly `s` (modulo characters
// that encoding can't represent, which round-trip to a replacement character either way), so it's pure
// wasted work. Requires both sides to reference the same encoding via the identical static property
// access (checked with IsEquivalentTo) — GetString(otherEncoding.GetBytes(s)) with a DIFFERENT encoding
// on each side is a real (if unusual) transcoding operation, not a no-op.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantEncodingRoundTripAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0165";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Encoding.GetString(Encoding.GetBytes(...)) round trip",
        messageFormat: "'{0}.GetString({0}.GetBytes(s))' encodes and immediately decodes 's' with the same encoding — use 's' directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Encoding.GetString(Encoding.GetBytes(s)), with the same encoding on both sides, round-trips s back to itself — the encode/decode work has no observable effect.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetString", Expression: var outerEncoding } getStringAccess)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax innerInvocation }]) return;

        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetBytes", Expression: var innerEncoding })
            return;
        if (innerInvocation.ArgumentList.Arguments.Count != 1) return;

        if (!outerEncoding.IsEquivalentTo(innerEncoding)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "GetString" } getStringMethod)
            return;
        if (getStringMethod.ContainingType?.ToDisplayString() != "System.Text.Encoding") return;

        if (context.SemanticModel.GetSymbolInfo(innerInvocation, ct).Symbol is not IMethodSymbol { Name: "GetBytes" } getBytesMethod)
            return;
        if (getBytesMethod.ContainingType?.ToDisplayString() != "System.Text.Encoding") return;

        var encodingSymbol = context.SemanticModel.GetSymbolInfo(outerEncoding, ct).Symbol;
        var displayName = encodingSymbol?.ToDisplayString() ?? getStringAccess.Expression.ToString();
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), displayName));
    }
}
