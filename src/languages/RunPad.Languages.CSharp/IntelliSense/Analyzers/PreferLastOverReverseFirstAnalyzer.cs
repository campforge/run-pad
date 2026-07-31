using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят первый проход, batch) — own pick, not a Roslynator port.
// `xs.Reverse().First()` (or FirstOrDefault) reverses the ENTIRE sequence — buffering every element —
// just to read what was originally the last one. `xs.Last()`/`xs.LastOrDefault()` reads the same
// element directly, without ever reversing anything.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferLastOverReverseFirstAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0122";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Last over Reverse().First()",
        messageFormat: "'Reverse().{0}()' can be simplified to '{1}()' — reversing the whole sequence just to read the first element is wasteful; the last element is the same thing",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Reverse().First()/FirstOrDefault() buffers and reverses the entire sequence just to read what was originally the last element — Last()/LastOrDefault() reads it directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> SuggestionByTerminalName = ImmutableDictionary.CreateRange(
    [
        new KeyValuePair<string, string>("First", "Last"),
        new KeyValuePair<string, string>("FirstOrDefault", "LastOrDefault"),
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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var terminalName } memberAccess)
            return;
        if (!SuggestionByTerminalName.TryGetValue(terminalName, out var suggestion)) return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } reverseInvocation)
            return;
        if (reverseInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Reverse" }) return;
        if (!IsRealEnumerableMethod(context, reverseInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), terminalName, suggestion));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
