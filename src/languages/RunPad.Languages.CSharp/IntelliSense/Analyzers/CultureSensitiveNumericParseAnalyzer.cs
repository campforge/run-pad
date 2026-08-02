using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят шестой проход, batch) — same culture-sensitivity theme as
// CultureSensitiveStringCompareAnalyzer/CultureSensitiveStringMethodAnalyzer/
// CultureSensitiveCaseConversionAnalyzer/CultureSensitiveCharCaseConversionAnalyzer
// (RPAD0005/RPAD0031/RPAD0039/RPAD0068), applied to numeric parsing instead of string comparison.
// `double.Parse(s)`/`float.Parse(s)`/`decimal.Parse(s)` with no IFormatProvider use
// CultureInfo.CurrentCulture — meaning the decimal separator alone varies by locale ("1.5" vs "1,5"),
// which is a classic source of bugs when parsing culture-invariant data (JSON numbers, config values,
// data from another system) on a machine set to a different locale. NOT extended to int/long.Parse —
// integers have no decimal separator, so NumberStyles.Integer's default parsing isn't
// culture-sensitive in the same way.
// Extended (двести сорок четвёртый проход, batch) to also cover `DateTime.Parse(s)`/
// `TimeSpan.Parse(s)` — same underlying gotcha, different symptom: DateTime's default parse is
// ambiguous about day/month ORDER ("01/02/2024" is Jan 2 in en-US but Feb 1 in most of Europe), and
// TimeSpan's default parse expects the current culture's own separators. Matched by containing-type
// NAME here rather than SpecialType, since neither DateTime nor TimeSpan has one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureSensitiveNumericParseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0098";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Culture-sensitive parse",
        messageFormat: "'{0}.Parse(...)' uses the current culture — pass CultureInfo.InvariantCulture unless you specifically need locale-aware parsing",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The single-argument Parse overloads on float/double/decimal/DateTime/TimeSpan all parse using CultureInfo.CurrentCulture, so the result can vary by locale.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> CultureSensitiveParseTypes = ImmutableHashSet.Create(
        SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal);

    private static readonly ImmutableHashSet<string> CultureSensitiveParseTypeNames = ImmutableHashSet.Create(
        "System.DateTime", "System.TimeSpan");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" }) return;
        if (invocation.ArgumentList.Arguments.Count != 1) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            { Name: "Parse", Parameters.Length: 1 } method)
        {
            return;
        }
        if (method.ContainingType is not { } type) return;
        if (!CultureSensitiveParseTypes.Contains(type.SpecialType) && !CultureSensitiveParseTypeNames.Contains(type.ToDisplayString()))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), type.Name));
    }
}
