using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести третий проход, batch) — own pick, not a Roslynator port.
// `sb.Length = 0` truncates a StringBuilder back to empty by reaching into its Length property
// directly — `sb.Clear()` says the same intent directly and is what the type's own API was designed
// for, without relying on the reader knowing that setting Length to zero is how you empty one.
// Extended (двести пятьдесят девятый проход, batch) to also cover `sb.Remove(0, sb.Length)` — removing
// every character from the start through the current length is the same truncate-to-empty operation,
// just spelled through Remove's start/count pair instead of the Length property.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferClearOverStringBuilderLengthZeroAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0191";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Clear() over setting StringBuilder.Length to zero",
        messageFormat: "'{0}' can be replaced with 'sb.Clear()' — Clear() says the intent directly instead of relying on the equivalent truncate-to-zero form",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "StringBuilder.Clear() truncates the builder back to empty, exactly like setting Length to 0 or removing from 0 through the current Length, but names the intent directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLengthAssignment, SyntaxKind.SimpleAssignmentExpression);
        context.RegisterSyntaxNodeAction(AnalyzeRemoveFullRange, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeLengthAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Length" })
            return;

        var ct = context.CancellationToken;
        var rightConstant = context.SemanticModel.GetConstantValue(assignment.Right, ct);
        if (rightConstant.Value is not 0) return;

        if (context.SemanticModel.GetSymbolInfo(assignment.Left, ct).Symbol is not IPropertySymbol { Name: "Length" } lengthProperty)
            return;
        if (lengthProperty.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), "sb.Length = 0"));
    }

    private static void AnalyzeRemoveFullRange(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Remove", Expression: var removeReceiver })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            { Expression: var startArg },
            { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: var lengthReceiver } },
            ])
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(startArg, ct).Value is not 0) return;
        if (!removeReceiver.IsEquivalentTo(lengthReceiver)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Remove" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "sb.Remove(0, sb.Length)"));
    }
}
