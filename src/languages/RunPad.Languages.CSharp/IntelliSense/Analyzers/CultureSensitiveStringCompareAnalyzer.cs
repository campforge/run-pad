using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 145) — idea ported (not copied — own implementation, see
// EmptyCatchClauseAnalyzer's own doc comment for the same rationale) from Roslynator's
// UseStringComparisonAnalyzer (RCS1155, Apache License 2.0, https://github.com/dotnet/roslynator),
// narrowed to its single most unambiguous case. The two-argument `string.Compare(a, b)` overload
// compares using CultureInfo.CurrentCulture, not ordinal comparison — a real, well-known .NET gotcha
// (the same class of bug already found and fixed once in this codebase's own history, for `SortText`
// — see ANALYSIS.md twenty-first pass): the exact same code sorts/compares differently depending on
// the machine's locale. Deliberately narrow (only the true 2-arg (string, string) overload, checked
// via the semantic model, not just argument-count guessing) — Compare's 3-/4-argument overloads that
// already take a StringComparison/CultureInfo are the author making a deliberate, explicit choice and
// are correctly left alone.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureSensitiveStringCompareAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0005";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Culture-sensitive string comparison",
        messageFormat: "'string.Compare(a, b)' uses the current culture — results can differ across " +
            "machines/locales; specify a StringComparison explicitly",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The two-argument string.Compare(a, b) overload compares using " +
            "CultureInfo.CurrentCulture, not ordinal comparison.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 2) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                Name: "Compare",
                ContainingType.SpecialType: SpecialType.System_String,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_String }],
            })
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
