using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `dict.ToDictionary(kv => kv.Key, kv => kv.Value)`, when `dict` is already a dictionary, round-trips
// every entry through two LINQ selectors just to rebuild the exact same key/value pairs — the
// `Dictionary<TKey, TValue>(IDictionary<TKey, TValue>)` copy constructor already does exactly that
// copy in one step.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDictionaryConstructorOverToDictionaryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0185";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the Dictionary copy constructor over ToDictionary(Key, Value)",
        messageFormat: "'dict.ToDictionary(kv => kv.Key, kv => kv.Value)' round-trips every entry through two selectors — use 'new Dictionary<TKey, TValue>(dict)' to copy it directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ToDictionary(kv => kv.Key, kv => kv.Value) over an existing dictionary rebuilds the exact same entries through two LINQ selectors — the Dictionary copy constructor does the same copy in one step.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> DictionaryLikeTypeNames = ImmutableHashSet.Create(
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToDictionary", Expression: var receiver })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            { Expression: SimpleLambdaExpressionSyntax { Parameter: var keyParam, Body: MemberAccessExpressionSyntax { Name.Identifier.Text: "Key" } keyBody } },
            { Expression: SimpleLambdaExpressionSyntax { Parameter: var valueParam, Body: MemberAccessExpressionSyntax { Name.Identifier.Text: "Value" } valueBody } },
            ])
        {
            return;
        }
        if (keyBody.Expression is not IdentifierNameSyntax keyReceiver || keyReceiver.Identifier.Text != keyParam.Identifier.Text)
            return;
        if (valueBody.Expression is not IdentifierNameSyntax valueReceiver || valueReceiver.Identifier.Text != valueParam.Identifier.Text)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToDictionary" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiver, ct).Type;
        if (receiverType?.OriginalDefinition.ToDisplayString() is not { } originalName || !DictionaryLikeTypeNames.Contains(originalName))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
