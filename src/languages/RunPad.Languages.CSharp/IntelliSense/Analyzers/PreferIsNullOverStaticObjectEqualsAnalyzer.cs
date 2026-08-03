using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят четвёртый проход, batch) — own pick, not a Roslynator port. A different
// shape than SuspiciousEqualsNullAnalyzer (RPAD0074, проход 162): that one catches the INSTANCE call
// `x.Equals(null)` (a genuine NullReferenceException risk if x is itself null). This one is the STATIC,
// two-argument `object.Equals(x, null)`/`object.ReferenceEquals(x, null)` form — safe (it never
// dereferences x), but needlessly roundabout: both exist specifically to test for null/reference
// equality without going through a type's own possibly-overloaded `==`, which is exactly what the `is
// null` pattern already does, more directly and without a method call.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferIsNullOverStaticObjectEqualsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0135";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer 'is null' over the static object null-check helpers",
        messageFormat: "Use 'is null'/'is not null' instead of 'object.{0}(...)' — it bypasses any overloaded '==' the same way, more directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "object.Equals(x, null)/object.ReferenceEquals(x, null) test for null without going through a possibly-overloaded == — the is null pattern does the same thing more directly.");

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
        var methodName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.Text: "Equals" or "ReferenceEquals" } access => access.Name.Identifier.Text,
            IdentifierNameSyntax { Identifier.Text: "Equals" or "ReferenceEquals" } id => id.Identifier.Text,
            _ => null,
        };
        if (methodName is null) return;

        if (invocation.ArgumentList.Arguments is not [{ Expression: var firstArg }, { Expression: var secondArg }])
            return;

        var firstIsNull = IsNullLiteral(firstArg);
        var secondIsNull = IsNullLiteral(secondArg);
        if (firstIsNull == secondIsNull) return; // need exactly one null argument

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_Object) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }

    private static bool IsNullLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression };
}
