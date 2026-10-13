using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.DefaultIfEmpty().First()`/`.Last()`/`.Single()` reaches for DefaultIfEmpty to avoid the
// "sequence contains no elements" exception, then picks an element — exactly what
// `xs.FirstOrDefault()`/`LastOrDefault()`/`SingleOrDefault()` already do directly: for a non-empty
// sequence DefaultIfEmpty() is a no-op passthrough, and for an empty one it injects a single
// default(T) that First/Last/Single then returns — the same outcome the *OrDefault method produces in
// one call, without materializing an extra one-element "default" sequence in between.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferOrDefaultOverDefaultIfEmptyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0202";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer FirstOrDefault/LastOrDefault/SingleOrDefault over DefaultIfEmpty().First/Last/Single",
        messageFormat: "'DefaultIfEmpty().{0}()' can be replaced with '{1}()' — it already handles the empty-sequence case directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "DefaultIfEmpty().First()/Last()/Single() and FirstOrDefault()/LastOrDefault()/SingleOrDefault() produce identical results for every input — the *OrDefault method does it in one call, without an intermediate DefaultIfEmpty sequence.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> OrDefaultMethod = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create("First", "FirstOrDefault"),
            KeyValuePair.Create("Last", "LastOrDefault"),
            KeyValuePair.Create("Single", "SingleOrDefault"),
        ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName, Expression: InvocationExpressionSyntax defaultIfEmptyInvocation })
            return;
        if (!OrDefaultMethod.TryGetValue(methodName, out var orDefaultName))
            return;
        if (invocation.ArgumentList.Arguments.Count != 0) return;

        if (defaultIfEmptyInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "DefaultIfEmpty" })
            return;
        if (defaultIfEmptyInvocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(defaultIfEmptyInvocation, ct).Symbol is not IMethodSymbol { Name: "DefaultIfEmpty" } defaultIfEmptyMethod)
            return;
        if (defaultIfEmptyMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, orDefaultName));
    }
}
