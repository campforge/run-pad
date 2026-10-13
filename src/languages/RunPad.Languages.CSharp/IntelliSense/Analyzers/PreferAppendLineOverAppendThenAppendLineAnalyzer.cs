using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Two shapes,
// same underlying idea — an underused StringBuilder.AppendLine overload:
// - `sb.Append(x).AppendLine()` appends `x`, then a separate call appends the line terminator —
//   `sb.AppendLine(x)` already does both in one call.
// - `sb.Append(Environment.NewLine)` spells out the line terminator by hand — `sb.AppendLine()`
//   (no arguments) already appends exactly that.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferAppendLineOverAppendThenAppendLineAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0180";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer AppendLine over a separate Append for the line terminator",
        messageFormat: "'{0}' can be simplified to '{1}'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "AppendLine(x) already appends x followed by a line terminator, and parameterless AppendLine() already appends Environment.NewLine — both avoid a separate Append call.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } memberAccess)
            return;

        var ct = context.CancellationToken;

        if (methodName == "AppendLine"
            && invocation.ArgumentList.Arguments.Count == 0
            && memberAccess.Expression is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } appendInvocation
            && appendInvocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Append" })
        {
            if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "AppendLine" } appendLineMethod)
                return;
            if (appendLineMethod.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;
            if (context.SemanticModel.GetSymbolInfo(appendInvocation, ct).Symbol is not IMethodSymbol { Name: "Append" } appendMethod)
                return;
            if (appendMethod.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Append(x).AppendLine()", "AppendLine(x)"));
            return;
        }

        if (methodName == "Append"
            && invocation.ArgumentList.Arguments is [{ Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "NewLine" } newLineAccess }])
        {
            if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Append" } appendMethod2)
                return;
            if (appendMethod2.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;
            if (context.SemanticModel.GetSymbolInfo(newLineAccess, ct).Symbol is not IPropertySymbol { Name: "NewLine" } newLineProperty)
                return;
            if (newLineProperty.ContainingType?.ToDisplayString() != "System.Environment") return;

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Append(Environment.NewLine)", "AppendLine()"));
        }
    }
}
