using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Path.Combine(Path.Combine(a, b), c)` nests two Path.Combine calls, building and discarding an
// intermediate combined path just to combine it again — Path.Combine has 3-argument, 4-argument, and
// params overloads, so `Path.Combine(a, b, c)` already produces the same joined path in one call.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferFlattenedPathCombineAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0208";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer a single flattened Path.Combine call",
        messageFormat: "'Path.Combine(Path.Combine(...), ...)' builds and discards an intermediate path — pass all the segments to a single 'Path.Combine(...)' call instead",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Path.Combine(Path.Combine(a, b), c) nests two calls where Path.Combine(a, b, c) (it has 3-arg/4-arg/params overloads) already combines every segment in one call.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Combine" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax innerInvocation }, ..])
            return;
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Combine" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Combine" } outerMethod)
            return;
        if (outerMethod.ContainingType?.ToDisplayString() != "System.IO.Path") return;

        if (context.SemanticModel.GetSymbolInfo(innerInvocation, ct).Symbol is not IMethodSymbol { Name: "Combine" } innerMethod)
            return;
        if (innerMethod.ContainingType?.ToDisplayString() != "System.IO.Path") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
