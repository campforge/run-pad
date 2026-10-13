using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Task.WhenAll(new[] { t1, t2 })`/`Task.WhenAny(new[] { t1, t2 })` build an array literal just to
// hand it to a method that also has a `params Task[]` overload — spelling the tasks out as individual
// arguments (`Task.WhenAll(t1, t2)`) calls that overload directly, without the explicit array literal.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantParamsArrayForTaskWhenAllAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0107";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant array literal for Task.WhenAll/WhenAny",
        messageFormat: "Pass the tasks directly to '{0}(...)' instead of wrapping them in an array literal — the params overload accepts them without one",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Task.WhenAll/WhenAny also have a params Task[] overload — an explicit array literal argument is unnecessary.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "WhenAll" or "WhenAny" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var argExpression }]) return;

        var hasNonEmptyInitializer = argExpression switch
        {
            ArrayCreationExpressionSyntax { Initializer.Expressions.Count: > 0 } => true,
            ImplicitArrayCreationExpressionSyntax { Initializer.Expressions.Count: > 0 } => true,
            _ => false,
        };
        if (!hasNonEmptyInitializer) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), method.Name));
    }
}
