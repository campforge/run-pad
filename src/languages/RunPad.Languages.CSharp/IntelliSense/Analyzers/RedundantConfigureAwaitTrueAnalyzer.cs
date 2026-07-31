using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят третий проход, batch) — own pick, not a Roslynator port.
// `task.ConfigureAwait(true)` explicitly requests exactly the behavior an unadorned `await task`
// already has — capturing and resuming on the current synchronization context. The call is a
// genuine no-op, not just a style nit.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantConfigureAwaitTrueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0130";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant ConfigureAwait(true)",
        messageFormat: "'.ConfigureAwait(true)' is a no-op — 'true' is already the default when ConfigureAwait isn't called at all; remove it",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ConfigureAwait(true) requests exactly the synchronization-context-capturing behavior an unadorned await already has.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> AwaitableTypeNames = ImmutableHashSet.Create(
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.Task<TResult>",
        "System.Threading.Tasks.ValueTask",
        "System.Threading.Tasks.ValueTask<TResult>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.TrueLiteralExpression } }])
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            { Name: "ConfigureAwait" } method)
        {
            return;
        }
        if (method.ContainingType?.OriginalDefinition.ToDisplayString() is not { } containingTypeName
            || !AwaitableTypeNames.Contains(containingTypeName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
