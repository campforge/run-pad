using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят девятый проход, batch) — own pick, not a Roslynator port.
// `await Task.Delay(0)` (or `Task.Delay(TimeSpan.Zero)`) is a common idiom for forcing a single
// asynchronous continuation point (yielding control back to the caller/scheduler without an actual
// delay) — but `Task.Delay` still goes through timer registration for a delay that's immediately zero.
// `await Task.Yield()` says the same intent directly and is built specifically for this.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferTaskYieldOverZeroDelayAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0112";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Task.Yield over Task.Delay(0)",
        messageFormat: "Use 'Task.Yield()' instead of 'Task.Delay(0)' — Yield() is the idiomatic way to force a single async continuation point",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Task.Delay(0)/Task.Delay(TimeSpan.Zero) is commonly used just to yield control once — Task.Yield() says that directly without timer registration.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Delay" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var delayArgument }]) return;

        var ct = context.CancellationToken;
        if (!IsZeroMillisecondsLiteral(delayArgument) && !IsTimeSpanZero(context, delayArgument, ct)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsZeroMillisecondsLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";

    private static bool IsTimeSpanZero(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, CancellationToken ct) =>
        expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Zero" } access
        && context.SemanticModel.GetSymbolInfo(access, ct).Symbol is IFieldSymbol { ContainingType: { } type }
        && type.ToDisplayString() == "System.TimeSpan";
}
