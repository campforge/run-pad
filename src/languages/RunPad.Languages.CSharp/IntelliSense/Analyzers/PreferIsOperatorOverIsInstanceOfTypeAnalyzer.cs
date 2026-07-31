using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `typeof(Y).IsInstanceOfType(x)` asks, via reflection, whether `x` is an instance of `Y` or a type
// derived from it — exactly what the `is` operator already checks, directly and without a Type object
// lookup. Deliberately restricted to a `typeof(...)` receiver (not `x.GetType().IsInstanceOfType(y)` —
// that's checking against x's own RUNTIME type, a different, non-constant target the `is` operator
// can't express the same way).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferIsOperatorOverIsInstanceOfTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0148";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer is over typeof(...).IsInstanceOfType(...)",
        messageFormat: "'typeof({0}).IsInstanceOfType(x)' can be simplified to 'x is {0}'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "typeof(Y).IsInstanceOfType(x) checks the same thing the is operator does, through reflection instead of directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "IsInstanceOfType", Expression: TypeOfExpressionSyntax typeOf })
            return;
        if (invocation.ArgumentList.Arguments.Count != 1) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "IsInstanceOfType" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Type") return;

        var targetType = context.SemanticModel.GetTypeInfo(typeOf.Type, ct).Type;
        if (targetType is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), targetType.Name));
    }
}
