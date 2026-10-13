using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Math.Min(Math.Max(x, min), max)`/`Math.Max(Math.Min(x, max), min)` spell out, by hand, exactly what
// Math.Clamp(x, min, max) already does — bound a value between a lower and upper limit. Math.Clamp
// (added in .NET Core 2.0) says the intent directly instead of making the reader reconstruct it from
// two nested calls, and it is immune to accidentally nesting Min/Max the wrong way round.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferMathClampOverNestedMinMaxAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0194";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Math.Clamp over nested Math.Min/Math.Max",
        messageFormat: "'Math.{0}(Math.{1}(x, ...), ...)' can be replaced with 'Math.Clamp(x, min, max)' — Clamp says the intent directly and can't have Min/Max nested the wrong way round",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Math.Min(Math.Max(x, min), max) and Math.Max(Math.Min(x, max), min) both bound a value between two limits — exactly what Math.Clamp(x, min, max) does directly, for the ordinary case where min <= max.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Min" or "Max" } outerAccess)
            return;
        var outerName = outerAccess.Name.Identifier.Text;
        var innerName = outerName == "Min" ? "Max" : "Min";

        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax innerInvocation }, _])
            return;
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var innerAccessName })
            return;
        if (innerAccessName != innerName) return;
        if (innerInvocation.ArgumentList.Arguments.Count != 2) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedOuterName } outerMethod
            || resolvedOuterName != outerName)
        {
            return;
        }
        if (outerMethod.ContainingType?.ToDisplayString() != "System.Math") return;

        if (context.SemanticModel.GetSymbolInfo(innerInvocation, ct).Symbol is not IMethodSymbol { Name: var resolvedInnerName } innerMethod
            || resolvedInnerName != innerName)
        {
            return;
        }
        if (innerMethod.ContainingType?.ToDisplayString() != "System.Math") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), outerName, innerName));
    }
}
