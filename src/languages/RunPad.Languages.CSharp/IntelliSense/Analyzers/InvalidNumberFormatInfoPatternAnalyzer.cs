using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Globalization.NumberFormatInfo`'s integer digit/pattern
// properties — an `int` literal assigned to `NumberDecimalDigits`/`CurrencyDecimalDigits`/
// `PercentDecimalDigits` (valid range 0-99), `CurrencyPositivePattern`/`PercentPositivePattern` (0-3),
// `CurrencyNegativePattern` (0-16 — confirmed empirically; NOT 15, which a naive reading of the exception
// message's phrasing might suggest), `PercentNegativePattern` (0-11), or `NumberNegativePattern` (0-4)
// throws `ArgumentOutOfRangeException` for a value outside that property's own range. Confirmed invariant
// across en-US/de-DE/ar-SA/ja-JP (a pure range check, unrelated to the instance's own culture data). No
// side effects. Same shape as this catalog's existing range-validated-property-assignment family
// (`NegativeCapacityArgumentAnalyzer`/`NegativeEnsureCapacityArgumentAnalyzer`/
// `NegativeListCapacityAssignmentAnalyzer`), applied to a new receiver type. Deliberately does NOT cover
// `NumberFormatInfo`'s `int[]`-valued `XxxGroupSizes` properties (a different, array-literal argument
// shape flagged as a separate, lower-priority candidate by the agent) or its string-valued separator/
// symbol properties (confirmed by the agent to be mostly unvalidated — most accept an empty string with no
// throw at all, and only `null` throws for the rest, which isn't a meaningful literal-content case).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidNumberFormatInfoPatternAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0439";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "NumberFormatInfo property assignment is out of range",
        messageFormat: "'NumberFormatInfo.{0} = {1}' always throws ArgumentOutOfRangeException at runtime — valid values are {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "NumberFormatInfo's integer digit/pattern properties (NumberDecimalDigits, CurrencyDecimalDigits, PercentDecimalDigits, CurrencyPositivePattern, PercentPositivePattern, CurrencyNegativePattern, PercentNegativePattern, NumberNegativePattern) throw ArgumentOutOfRangeException for a value outside that property's own valid range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    // Display-only — the actual throw/no-throw verdict always comes from calling the real property
    // setter (see TryGetRange below), never from these numbers; they only format the diagnostic message,
    // reusing the exact bounds already confirmed empirically (CurrencyNegativePattern's upper bound is 16,
    // not the 15 a naive reading of .NET's own exception message phrasing might suggest).
    private static readonly ImmutableDictionary<string, string> RangeText = new Dictionary<string, string>
    {
        ["NumberDecimalDigits"] = "0-99",
        ["CurrencyDecimalDigits"] = "0-99",
        ["PercentDecimalDigits"] = "0-99",
        ["CurrencyPositivePattern"] = "0-3",
        ["PercentPositivePattern"] = "0-3",
        ["CurrencyNegativePattern"] = "0-16",
        ["PercentNegativePattern"] = "0-11",
        ["NumberNegativePattern"] = "0-4",
    }.ToImmutableDictionary();

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName } access)
            return;
        if (!RangeText.TryGetValue(propertyName, out var range)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int value) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName } property)
            return;
        if (symbolName != propertyName) return;
        if (property.Type.SpecialType != SpecialType.System_Int32) return;

        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Globalization.NumberFormatInfo") return;

        if (!AlwaysThrows(propertyName, value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, value, range));
    }

    private static bool AlwaysThrows(string propertyName, int value)
    {
        var info = new NumberFormatInfo();
        try
        {
            switch (propertyName)
            {
                case "NumberDecimalDigits": info.NumberDecimalDigits = value; break;
                case "CurrencyDecimalDigits": info.CurrencyDecimalDigits = value; break;
                case "PercentDecimalDigits": info.PercentDecimalDigits = value; break;
                case "CurrencyPositivePattern": info.CurrencyPositivePattern = value; break;
                case "PercentPositivePattern": info.PercentPositivePattern = value; break;
                case "CurrencyNegativePattern": info.CurrencyNegativePattern = value; break;
                case "PercentNegativePattern": info.PercentNegativePattern = value; break;
                case "NumberNegativePattern": info.NumberNegativePattern = value; break;
            }
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
