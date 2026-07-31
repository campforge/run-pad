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
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CultureSensitiveNumericParseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0098";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Culture-sensitive numeric parse",
        messageFormat: "'{0}.Parse(...)' uses the current culture's decimal separator — pass CultureInfo.InvariantCulture unless you specifically need locale-aware parsing",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The single-argument floating-point/decimal Parse overloads parse using CultureInfo.CurrentCulture, so the decimal separator itself varies by locale.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> CultureSensitiveParseTypes = ImmutableHashSet.Create(
        SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal);

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
        if (method.ContainingType is not { } type || !CultureSensitiveParseTypes.Contains(type.SpecialType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), type.Name));
    }
}
