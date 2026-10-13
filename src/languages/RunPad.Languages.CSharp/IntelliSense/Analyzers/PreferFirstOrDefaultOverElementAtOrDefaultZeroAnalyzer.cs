using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.ElementAtOrDefault(0)` reaches for the general "get the element at this index, or default"
// method just to ask for the first one — `xs.FirstOrDefault()` says that directly and is the more
// common, more readable spelling for the same operation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferFirstOrDefaultOverElementAtOrDefaultZeroAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0212";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer FirstOrDefault over ElementAtOrDefault(0)",
        messageFormat: "'ElementAtOrDefault(0)' can be replaced with 'FirstOrDefault()' — same result, more direct",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ElementAtOrDefault(0) and FirstOrDefault() return the same value for every input — FirstOrDefault() says the intent directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ElementAtOrDefault" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var indexArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not 0) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ElementAtOrDefault" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
