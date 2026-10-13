using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `dict.Select(kv => kv.Key)`/`dict.Select(kv => kv.Value)` projects each entry down to one half of
// it through a LINQ pass — `dict.Keys`/`dict.Values` already expose exactly that as a live view,
// without an enumerator-driven projection over every entry.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferKeysOrValuesOverSelectAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0184";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Keys/Values over Select(kv => kv.Key/Value)",
        messageFormat: "'Select(kv => kv.{0})' can be simplified to '.{1}' — the dictionary already exposes that view directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "dict.Select(kv => kv.Key)/dict.Select(kv => kv.Value) project each entry through a LINQ pass — dict.Keys/dict.Values already expose the same elements directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select", Expression: var receiver })
            return;
        if (invocation.ArgumentList.Arguments is not
            [{ Expression: SimpleLambdaExpressionSyntax { Parameter: var param, Body: MemberAccessExpressionSyntax { Name.Identifier.Text: "Key" or "Value" } bodyAccess } }])
        {
            return;
        }
        if (bodyAccess.Expression is not IdentifierNameSyntax bodyReceiver || bodyReceiver.Identifier.Text != param.Identifier.Text)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Select" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiver, ct).Type;
        if (receiverType?.OriginalDefinition.ToDisplayString() is not { } originalName || !DictionaryLikeTypeNames.Contains(originalName))
            return;

        var accessedName = bodyAccess.Name.Identifier.Text;
        var suggestion = accessedName == "Key" ? "Keys" : "Values";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), accessedName, suggestion));
    }
}
