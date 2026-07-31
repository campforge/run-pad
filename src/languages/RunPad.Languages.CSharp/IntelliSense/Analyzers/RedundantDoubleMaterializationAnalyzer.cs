using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят третий проход, batch) — own pick, not a Roslynator port.
// `xs.ToList().ToArray()` (or the reverse, `xs.ToArray().ToList()`) materializes the sequence TWICE —
// once into a List, then copies every element again into an Array (or vice versa) — when calling the
// outer method directly on the original sequence produces the same result in one materialization.
// List<T>.ToArray() is its own INSTANCE method (not the Enumerable.ToArray() extension), so both that
// and the extension form need to be recognized as "a real materialization call" here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantDoubleMaterializationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0083";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant double materialization",
        messageFormat: "'{1}().{0}()' materializes the sequence twice — call '{0}()' directly on the original sequence instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ToList().ToArray() (or ToArray().ToList()) copies the sequence's elements twice; calling the outer method directly on the original sequence does it once.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> OppositeMaterializer = ImmutableDictionary.CreateRange(
    [
        new KeyValuePair<string, string>("ToList", "ToArray"),
        new KeyValuePair<string, string>("ToArray", "ToList"),
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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var outerName } memberAccess)
            return;
        if (!OppositeMaterializer.TryGetValue(outerName, out var innerExpectedName)) return;

        var ct = context.CancellationToken;
        if (!IsMaterializationMethod(context, invocation, outerName, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } innerInvocation)
            return;
        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var innerName } || innerName != innerExpectedName)
            return;
        if (!IsMaterializationMethod(context, innerInvocation, innerExpectedName, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), outerName, innerExpectedName));
    }

    private static bool IsMaterializationMethod(
        SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, string expectedName, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        if (method.Name != expectedName) return false;

        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable") return true;
        return expectedName == "ToArray" && original.ContainingType?.ToDisplayString() == "System.Collections.Generic.List<T>";
    }
}
