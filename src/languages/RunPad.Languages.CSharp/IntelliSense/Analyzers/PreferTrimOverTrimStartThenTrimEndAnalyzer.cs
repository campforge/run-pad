using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести двадцать восьмой проход, batch) — own pick, not a Roslynator port.
// `s.TrimStart().TrimEnd()` (or `s.TrimEnd().TrimStart()`) trims one end, allocates a new string, then
// trims the other end of that new string — `s.Trim()` already trims both ends in one call, without the
// intermediate allocation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferTrimOverTrimStartThenTrimEndAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0213";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Trim() over chaining TrimStart() and TrimEnd()",
        messageFormat: "'{0}().{1}()' can be replaced with 'Trim()' — it trims both ends in one call, without the intermediate string",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "TrimStart().TrimEnd() (in either order) trims one end, allocates an intermediate string, then trims the other end of it — Trim() trims both ends directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "TrimStart" or "TrimEnd" } outerAccess)
            return;
        var outerName = outerAccess.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (outerAccess.Expression is not InvocationExpressionSyntax innerInvocation) return;

        var expectedInnerName = outerName == "TrimStart" ? "TrimEnd" : "TrimStart";
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var innerName } || innerName != expectedInnerName)
            return;
        if (innerInvocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedOuterName } outerMethod
            || resolvedOuterName != outerName)
        {
            return;
        }
        if (outerMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        if (context.SemanticModel.GetSymbolInfo(innerInvocation, ct).Symbol is not IMethodSymbol { Name: var resolvedInnerName } innerMethod
            || resolvedInnerName != innerName)
        {
            return;
        }
        if (innerMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), innerName, outerName));
    }
}
