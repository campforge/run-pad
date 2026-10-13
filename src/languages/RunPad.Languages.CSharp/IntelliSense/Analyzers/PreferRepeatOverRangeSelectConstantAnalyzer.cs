using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Enumerable.Range(start, count).Select(i => 0)` builds a sequence of indices only to throw the index
// away and project every element to the same constant — `Enumerable.Repeat(0, count)` says that
// directly. Deliberately restricted to a LITERAL lambda body (not "any expression that doesn't
// reference the index"): a non-literal body like `i => Guid.NewGuid()` doesn't depend on `i` either,
// but Select still re-evaluates it once per element while Repeat would evaluate it once and repeat the
// same value — a real behavioral difference for anything non-deterministic or side-effecting. A literal
// is the only shape where "doesn't depend on i" and "produces the same value every time" both hold
// unconditionally.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferRepeatOverRangeSelectConstantAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0201";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Enumerable.Repeat over Range(...).Select(i => constant)",
        messageFormat: "'Range(...).Select(i => constant)' builds and discards an index sequence just to project every element to the same value — 'Enumerable.Repeat(constant, count)' says that directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Range(start, count).Select(i => constant) projects every element of an index sequence to the same literal, discarding the index — Enumerable.Repeat(constant, count) expresses the same sequence directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select", Expression: InvocationExpressionSyntax rangeInvocation })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: SimpleLambdaExpressionSyntax { Body: LiteralExpressionSyntax } }])
            return;

        if (rangeInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Range" })
            return;
        if (rangeInvocation.ArgumentList.Arguments.Count != 2) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Select" } selectMethod)
            return;
        if (selectMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(rangeInvocation, ct).Symbol is not IMethodSymbol { Name: "Range" } rangeMethod)
            return;
        if (rangeMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
