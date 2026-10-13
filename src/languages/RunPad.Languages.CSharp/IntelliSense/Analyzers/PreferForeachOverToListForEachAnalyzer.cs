using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.ToList().ForEach(action)` materializes a whole new List<T> just to immediately walk it once and
// throw it away — a plain `foreach` loop over `xs` runs the same action on the same elements without
// the extra allocation and copy. Scoped to the case where `xs` is not already a List<T> (a receiver
// that's already a List<T> doesn't need ToList() at all, and that's RemoveRedundantMaterialization-
// BeforeCountAnalyzer/RedundantDoubleMaterializationAnalyzer's kind of territory, not this one's).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferForeachOverToListForEachAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0144";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer foreach over ToList().ForEach()",
        messageFormat: "'.ToList().ForEach(...)' materializes a whole list just to iterate it once — a 'foreach' loop does the same work without the extra allocation",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ToList().ForEach(action) copies the whole sequence into a new List<T> only to walk it once and discard it — foreach iterates the original sequence directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ForEach" } forEachAccess)
            return;
        if (invocation.ArgumentList.Arguments.Count != 1) return;

        if (forEachAccess.Expression is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ToList" } toListAccess,
            } toListInvocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(toListInvocation, ct).Symbol is not IMethodSymbol { Name: "ToList" } toListMethod)
            return;
        if (toListMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ForEach" } forEachMethod)
            return;
        if (forEachMethod.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
            return;

        var receiverType = context.SemanticModel.GetTypeInfo(toListAccess.Expression, ct).Type;
        if (receiverType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
