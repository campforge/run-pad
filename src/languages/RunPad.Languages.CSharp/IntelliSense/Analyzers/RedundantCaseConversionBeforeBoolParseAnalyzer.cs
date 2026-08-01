using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести восьмой проход, batch) — own pick, not a Roslynator port.
// `bool.Parse(s.ToUpperInvariant())`/`bool.TryParse(s.ToLowerInvariant(), out _)` normalize casing
// before parsing, but bool.Parse/TryParse already accept "True"/"true"/"TRUE" (and the False forms)
// interchangeably — confirmed empirically before writing this analyzer, not assumed. Restricted to the
// *Invariant forms specifically: plain ToUpper()/ToLower() already gets a culture-sensitivity warning
// from CultureSensitiveStringMethodAnalyzer on the same span for an unrelated reason, and stacking a
// second diagnostic there would just be two squiggles on one span for the reader to untangle.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantCaseConversionBeforeBoolParseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0195";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant case conversion before bool.Parse/TryParse",
        messageFormat: "'{0}()' before 'bool.{1}' is redundant — bool.Parse/TryParse already accept \"True\"/\"true\"/\"TRUE\" (and the False forms) interchangeably",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "bool.Parse and bool.TryParse already ignore case, so normalizing the input's case with ToUpperInvariant()/ToLowerInvariant() beforehand has no effect on the result.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" or "TryParse" } outerAccess)
            return;
        var outerName = outerAccess.Name.Identifier.Text;

        if (invocation.ArgumentList.Arguments is not [{ Expression: InvocationExpressionSyntax caseInvocation }, ..])
            return;
        if (caseInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToUpperInvariant" or "ToLowerInvariant" } caseAccess)
            return;
        var caseMethodName = caseAccess.Name.Identifier.Text;
        if (caseInvocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedOuterName } outerMethod
            || resolvedOuterName != outerName)
        {
            return;
        }
        if (outerMethod.ContainingType?.SpecialType != SpecialType.System_Boolean) return;

        if (context.SemanticModel.GetSymbolInfo(caseInvocation, ct).Symbol is not IMethodSymbol { Name: var resolvedCaseName } caseMethod
            || resolvedCaseName != caseMethodName)
        {
            return;
        }
        if (caseMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), caseMethodName, outerName));
    }
}
