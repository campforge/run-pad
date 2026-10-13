using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. GC.Collect()
// forces a full garbage collection outside the generational collector's own scheduling — almost always
// the wrong fix for a real problem (a leak, oversized allocations, GC pressure from short-lived
// objects) and actively hurts throughput by collecting generations that didn't need it yet. The one
// legitimate use (measuring allocations in a benchmark) is rare enough that flagging every call and
// asking "did you mean this" is the right default.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ManualGCCollectAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0132";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Manual GC.Collect() call",
        messageFormat: "Explicit 'GC.Collect()' call — the generational garbage collector already schedules its own collections; forcing one usually masks a different problem and hurts throughput",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Manual GC.Collect() calls force a full collection outside the GC's own scheduling and are rarely the right fix.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Collect" }) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            { Name: "Collect" } method)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.GC") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
