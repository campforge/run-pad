using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// Same idea as PreferListSearchMethodOverLinqEquivalentAnalyzer (RPAD0189), applied to
// arrays: `Array` ships static predicate-based search methods that predate LINQ and do the same job
// without an `IEnumerable<T>` iterator in between — `Array.Exists(arr, match)` for `arr.Any(predicate)`,
// `Array.TrueForAll(arr, match)` for `arr.All(predicate)`, `Array.Find(arr, match)` for the
// single-argument `arr.FirstOrDefault(predicate)`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferArraySearchMethodOverLinqEquivalentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0192";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Array's own static search method over the LINQ equivalent",
        messageFormat: "'{0}(predicate)' on an array can be replaced with the static 'Array.{1}(arr, predicate)' — Array's own method does the same search without going through an IEnumerable<T> iterator",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Array.Exists/TrueForAll/Find predate LINQ and do the same job as Any/All/FirstOrDefault without wrapping the array in an IEnumerable<T> iterator.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> LinqToArrayMethod = ImmutableDictionary.CreateRange(
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
        if (!LinqToArrayMethod.TryGetValue(methodName, out var arrayMethodName))
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
        if (receiverType is not IArrayTypeSymbol { Rank: 1 }) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, arrayMethodName));
    }
}
