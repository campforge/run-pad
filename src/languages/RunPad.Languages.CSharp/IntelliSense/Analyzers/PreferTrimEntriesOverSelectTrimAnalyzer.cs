using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести тринадцатый проход, batch) — own pick, not a Roslynator port. Same
// "StringSplitOptions already does this in the Split call itself" theme as
// PreferRemoveEmptyEntriesOverWhereLengthAnalyzer (RPAD0198, проход 212), for the trimming sibling:
// `s.Split(sep).Select(x => x.Trim())` trims every entry in a second LINQ pass — exactly what
// `s.Split(sep, StringSplitOptions.TrimEntries)` (.NET 5+) already does directly in the Split call.
// Skipped when Split already has a StringSplitOptions argument, same reasoning as RPAD0198.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferTrimEntriesOverSelectTrimAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0199";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer StringSplitOptions.TrimEntries over Select(x => x.Trim())",
        messageFormat: "'Split(...).Select(x => x.Trim())' can be replaced with 'Split(..., StringSplitOptions.TrimEntries)' — Split already trims entries when asked to",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Split(sep).Select(x => x.Trim()) trims every entry in a second LINQ pass — Split(sep, StringSplitOptions.TrimEntries) does the same trimming directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Select", Expression: InvocationExpressionSyntax splitInvocation })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            {
                Expression: SimpleLambdaExpressionSyntax
                {
                    Parameter: var param,
                    Body: InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Trim", Expression: IdentifierNameSyntax trimReceiverId },
                        ArgumentList.Arguments.Count: 0,
                    },
                },
            },
            ])
        {
            return;
        }
        if (trimReceiverId.Identifier.Text != param.Identifier.Text) return;

        if (splitInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Split" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Select" } selectMethod)
            return;
        if (selectMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(splitInvocation, ct).Symbol is not IMethodSymbol { Name: "Split" } splitMethod)
            return;
        if (splitMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        var hasOptionsArg = splitInvocation.ArgumentList.Arguments.Any(a =>
            context.SemanticModel.GetTypeInfo(a.Expression, ct).Type?.ToDisplayString() == "System.StringSplitOptions");
        if (hasOptionsArg) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
