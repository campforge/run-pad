using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести двенадцатый проход, batch) — own pick, not a Roslynator port.
// `s.Split(sep).Where(x => x.Length > 0)`/`s.Split(sep).Where(x => x.Length != 0)` filters out the
// empty entries a naive Split leaves behind (consecutive separators, leading/trailing separators) —
// exactly what `s.Split(sep, StringSplitOptions.RemoveEmptyEntries)` already does in the Split call
// itself, without a second LINQ pass. Skipped when Split already has a StringSplitOptions argument, to
// avoid the more involved job of merging flags.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferRemoveEmptyEntriesOverWhereLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0198";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer StringSplitOptions.RemoveEmptyEntries over Where(x => x.Length > 0)",
        messageFormat: "'Split(...).Where(x => x.Length > 0)' can be replaced with 'Split(..., StringSplitOptions.RemoveEmptyEntries)' — Split already filters empty entries when asked to",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Split(sep).Where(x => x.Length > 0) (or != 0) filters out empty entries in a second LINQ pass — Split(sep, StringSplitOptions.RemoveEmptyEntries) does the same filtering directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Where", Expression: InvocationExpressionSyntax splitInvocation })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: SimpleLambdaExpressionSyntax { Parameter: var param, Body: ExpressionSyntax body } }])
            return;
        if (!IsNonEmptyLengthCheck(body, param.Identifier.Text))
            return;

        if (splitInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Split" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Where" } whereMethod)
            return;
        if (whereMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(splitInvocation, ct).Symbol is not IMethodSymbol { Name: "Split" } splitMethod)
            return;
        if (splitMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        var hasOptionsArg = splitInvocation.ArgumentList.Arguments.Any(a =>
            context.SemanticModel.GetTypeInfo(a.Expression, ct).Type?.ToDisplayString() == "System.StringSplitOptions");
        if (hasOptionsArg) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsNonEmptyLengthCheck(ExpressionSyntax body, string paramName)
    {
        if (body is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.GreaterThanExpression or (int)SyntaxKind.NotEqualsExpression } binary)
            return false;
        if (binary.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: IdentifierNameSyntax leftId })
            return false;
        if (leftId.Identifier.Text != paramName) return false;

        return binary.Right is LiteralExpressionSyntax { Token.Value: 0 };
    }
}
