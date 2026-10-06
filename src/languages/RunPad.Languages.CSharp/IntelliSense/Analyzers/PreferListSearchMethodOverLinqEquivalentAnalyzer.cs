using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести первый проход, batch) — own pick, not a Roslynator port.
// `List<T>` ships its own predicate-based search methods that predate LINQ and do the exact same job
// without an `IEnumerable<T>` iterator/delegate-wrapper in between: `Exists(match)` for `Any(predicate)`,
// `TrueForAll(match)` for `All(predicate)`, and `Find(match)` for the single-argument
// `FirstOrDefault(predicate)` (both return `default(T)` when nothing matches, so they agree exactly).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferListSearchMethodOverLinqEquivalentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0189";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer List<T>'s own search method over the LINQ equivalent",
        messageFormat: "'{0}(predicate)' on a List<T> can be replaced with '{1}(predicate)' — List<T>'s own method does the same search without going through an IEnumerable<T> iterator",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "List<T>.Exists/TrueForAll/Find predate LINQ and do the same job as Any/All/FirstOrDefault without wrapping the list in an IEnumerable<T> iterator.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> LinqToListMethod = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create("Any", "Exists"),
            KeyValuePair.Create("All", "TrueForAll"),
            KeyValuePair.Create("FirstOrDefault", "Find"),
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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName, Expression: var receiver })
            return;
        if (!LinqToListMethod.TryGetValue(methodName, out var listMethodName))
            return;
        if (invocation.ArgumentList.Arguments.Count != 1) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiver, ct).Type;
        if (receiverType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, listMethodName));
    }
}
