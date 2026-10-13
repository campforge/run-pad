using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Reverse().Reverse()` reverses the sequence twice, which puts every element back in its original
// order — the whole pair is a no-op wrapped around two extra iterators.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantDoubleReverseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0077";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant double Reverse",
        messageFormat: "'Reverse().Reverse()' restores the original order — remove both calls",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Calling Reverse() twice in a row puts every element back in its original order — the pair is a no-op.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Reverse" })
            return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } innerInvocation)
            return;
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Reverse" }) return;
        if (!IsRealEnumerableMethod(context, innerInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
