using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.SelectMany(x => new[] { x })` wraps each element in a fresh one-element array only to flatten it
// right back out — the result is exactly `xs` again, element for element, just with an array allocated
// and discarded per element along the way.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantSelectManySingleElementArrayAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0170";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant SelectMany with a single-element array selector",
        messageFormat: "This SelectMany wraps each element in a single-element array only to immediately flatten it back out — use the sequence directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "SelectMany(x => new[] { x }) wraps each element in a one-element array only to flatten it back out — the result is the original sequence, with a wasted array allocation per element.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SelectMany" })
            return;
        if (invocation.ArgumentList.Arguments is not
            [{ Expression: SimpleLambdaExpressionSyntax { Parameter: var param, Body: var body } }])
        {
            return;
        }

        var elements = body switch
        {
            ArrayCreationExpressionSyntax { Initializer.Expressions: var exprs } => exprs,
            ImplicitArrayCreationExpressionSyntax { Initializer.Expressions: var exprs } => exprs,
            _ => default,
        };
        if (elements is not [IdentifierNameSyntax singleElement]) return;
        if (singleElement.Identifier.Text != param.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "SelectMany" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
