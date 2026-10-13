using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `dict.Keys.Contains(k)` goes through the Keys collection's enumerator/Contains implementation to
// answer a question the dictionary itself can answer directly and more clearly — `dict.ContainsKey(k)`
// says exactly what's meant, without the indirection through a derived view collection. Matched
// structurally (`x.Keys.Contains(k)`) plus a semantic check that the receiver of `.Keys` actually has
// a matching `ContainsKey` method, to stay safe for lookalike APIs.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DictionaryKeysContainsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0079";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use ContainsKey instead of Keys.Contains",
        messageFormat: "Use 'ContainsKey(...)' instead of 'Keys.Contains(...)' — goes directly to the dictionary instead of through its Keys view",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "dict.Keys.Contains(k) checks membership through the Keys view collection; dict.ContainsKey(k) answers the same question directly.");

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
        if (invocation.ArgumentList.Arguments is not [_]) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Contains",
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Keys" } keysAccess,
            })
        {
            return;
        }

        var dictType = context.SemanticModel.GetTypeInfo(keysAccess.Expression, context.CancellationToken).Type;
        if (dictType is null || !HasContainsKeyMethod(dictType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool HasContainsKeyMethod(ITypeSymbol type)
    {
        if (HasOwnContainsKeyMethod(type)) return true;
        return type.AllInterfaces.Any(HasOwnContainsKeyMethod);
    }

    private static bool HasOwnContainsKeyMethod(ITypeSymbol type) =>
        type.GetMembers("ContainsKey").OfType<IMethodSymbol>().Any(m => m.Parameters.Length == 1);
}
