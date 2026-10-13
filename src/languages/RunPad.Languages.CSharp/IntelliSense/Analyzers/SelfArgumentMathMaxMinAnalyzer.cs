using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "combining/comparing a value with itself is almost certainly a mistake" theme as
// SelfEqualityCheckAnalyzer (RPAD0096, проход 166) and SelfSubtractionOrXorAnalyzer (RPAD0142, проход
// 176), for a different shape: `Math.Max(x, x)`/`Math.Min(x, x)` always evaluates to x's own value (even
// for NaN — Max/Min propagate NaN, and x compared against itself is still x either way), so the call is
// a pointless no-op — almost certainly meant to compare x against something else. Deliberately
// restricted to a bare identifier in both argument positions, same as RPAD0096/RPAD0142 — two separate
// calls that happen to look identical aren't necessarily the same value if either has side effects.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SelfArgumentMathMaxMinAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0225";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Math.Max/Math.Min called with the same argument twice",
        messageFormat: "'Math.{0}(x, x)' always evaluates to x's own value — likely a copy-paste mistake",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Math.Max(x, x) and Math.Min(x, x) always return x unchanged, regardless of its value; this compiles fine but is almost never the intended call.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Max" or "Min" } access)
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            { Expression: IdentifierNameSyntax firstId },
            { Expression: IdentifierNameSyntax secondId },
            ])
        {
            return;
        }
        if (firstId.Identifier.Text != secondId.Identifier.Text) return;

        var methodName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Math") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
