using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `sb.Append(x.ToString())` calls ToString() explicitly before handing the result to
// StringBuilder.Append — but StringBuilder.Append has an overload for essentially every type
// (including a plain `object` fallback that calls ToString() internally anyway), so the explicit call
// is always redundant regardless of x's type. Deliberately skips the case where x's static type is
// already string — RemoveRedundantToStringCallAnalyzer (RPAD0012) already flags that (ToString() on an
// already-string value is redundant everywhere, not just before Append), and firing both here would
// double-message the same span.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantToStringBeforeStringBuilderAppendAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0063";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant ToString() before StringBuilder.Append",
        messageFormat: "Calling ToString() before StringBuilder.Append is redundant — Append has an overload that accepts the value directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "StringBuilder.Append has an overload for essentially every type, so an explicit ToString() call before it is always redundant.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Append" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax toStringCall }]) return;
        if (toStringCall.ArgumentList.Arguments.Count != 0) return;
        if (toStringCall.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" } toStringAccess)
            return;

        var ct = context.CancellationToken;

        if (context.SemanticModel.GetSymbolInfo(toStringCall, ct).Symbol is not IMethodSymbol { Parameters.Length: 0, Name: "ToString" })
            return;

        var argType = context.SemanticModel.GetTypeInfo(toStringAccess.Expression, ct).Type;
        if (argType is { SpecialType: SpecialType.System_String }) return; // RPAD0012's territory

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol appendMethod) return;
        if (appendMethod.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
