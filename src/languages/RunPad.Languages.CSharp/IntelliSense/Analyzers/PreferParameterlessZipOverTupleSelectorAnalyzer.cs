using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят девятый проход, batch) — own pick, not a Roslynator port.
// `xs.Zip(ys, (a, b) => (a, b))` spells out a result selector that does nothing but re-pack its two
// parameters into a tuple — exactly what the parameterless `Zip(IEnumerable<TSecond>)` overload
// (added in .NET Core 3.0, well within this project's .NET 6 SDK floor) already returns directly.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferParameterlessZipOverTupleSelectorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0154";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant tuple-packing selector on Zip",
        messageFormat: "'Zip(ys, (a, b) => (a, b))' can be simplified to 'Zip(ys)' — the parameterless overload already returns a sequence of tuples",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A Zip result selector that just re-packs its two parameters into a tuple duplicates what the parameterless Zip overload already returns.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Zip" }) return;
        if (invocation.ArgumentList.Arguments is not
            [_, { Expression: ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: [var p1, var p2] } lambda }])
        {
            return;
        }

        if (lambda.Body is not TupleExpressionSyntax
            {
                Arguments:
                [
                { Expression: IdentifierNameSyntax firstArg, NameColon: null },
                { Expression: IdentifierNameSyntax secondArg, NameColon: null },
                ],
            })
        {
            return;
        }
        if (firstArg.Identifier.Text != p1.Identifier.Text || secondArg.Identifier.Text != p2.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Zip" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
