using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Task.FromResult(x).Result` wraps a value in a completed Task just to immediately unwrap it again —
// the Task is thrown away right after construction, so the whole round trip is equivalent to just `x`.
// Unlike TaskResultOrGetAwaiterGetResultInAsyncMethodAnalyzer (RPAD0131), this isn't about
// blocking risk — Task.FromResult's Task is already completed, so `.Result` never actually blocks —
// it's pure redundancy, and applies in any method, async or not.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantTaskFromResultThenResultAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0140";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Task.FromResult(...).Result round trip",
        messageFormat: "'Task.FromResult(x).Result' wraps and immediately unwraps 'x' — use 'x' directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Task.FromResult(x).Result wraps x in a completed Task only to unwrap it again immediately — the Task never escapes, so the round trip is equivalent to x.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Name.Identifier.Text != "Result") return;

        if (memberAccess.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "FromResult" },
                ArgumentList.Arguments: [_],
            } fromResultInvocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(memberAccess, ct).Symbol is not IPropertySymbol { Name: "Result" } resultProperty)
            return;
        if (resultProperty.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Threading.Tasks.Task<TResult>")
            return;

        if (context.SemanticModel.GetSymbolInfo(fromResultInvocation, ct).Symbol is not IMethodSymbol
            { Name: "FromResult", IsStatic: true } fromResultMethod)
        {
            return;
        }
        if (fromResultMethod.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation()));
    }
}
