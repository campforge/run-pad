using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #6 from the twelfth research subagent survey, re-verified
// independently before implementing. `System.Xml.XmlReaderSettings` — 2 enum property setters
// (`DtdProcessing`, `ValidationType`) and 2 `long` property setters (`MaxCharactersInDocument`,
// `MaxCharactersFromEntities`), all on a throwaway `new XmlReaderSettings()`. Confirmed empirically: the
// enum properties throw `ArgumentOutOfRangeException` for a value outside their own enum's defined range
// (same shape as `InvalidXmlWriterSettingsPropertyAnalyzer`, RPAD0444, its sibling settings type); the two
// `long` properties throw `ArgumentOutOfRangeException` for a negative value (0 means "no limit", so is
// itself valid, unlike a genuinely negative count). Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No
// side effects. Kept as its own analyzer/type rather than merged with RPAD0444, mirroring how this catalog
// already splits per-declaring-type instead of combining siblings across two distinct settings types.
// Extended (453-й проход, candidate #3 from the fifteenth research subagent survey) to also cover
// `ValidationFlags` (a `[Flags]` `XmlSchemaValidationFlags`) — confirmed empirically it throws
// `ArgumentOutOfRangeException` for any bit pattern beyond the OR of all 5 defined flags (31), or a
// negative value; confirmed state-independent (identical throw behavior whether `ValidationType` is left
// at its default `None` or set to something else first, ruling out the schema/state-dependence trap).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlReaderSettingsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0445";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlReaderSettings property assignment is invalid",
        messageFormat: "'XmlReaderSettings.{0} = {1}' always throws ArgumentOutOfRangeException at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlReaderSettings.DtdProcessing/ValidationType throw for an out-of-range enum value, and MaxCharactersInDocument/MaxCharactersFromEntities throw for a negative value.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    // Property name -> its enum type's own Name. "ValidationFlags" is the one case where these differ
    // (its type is XmlSchemaValidationFlags, not "ValidationFlags") — self-caught before shipping: an
    // initial draft compared the enum type name directly against the property name (true by coincidence
    // for DtdProcessing/ValidationType, whose enum type names match their property names), which
    // silently never matched for ValidationFlags and produced zero diagnostics despite GetConstantValue
    // and the real-API probe both working correctly in isolation.
    private static readonly ImmutableDictionary<string, string> EnumPropertyTypeNames = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create("DtdProcessing", "DtdProcessing"),
            KeyValuePair.Create("ValidationType", "ValidationType"),
            KeyValuePair.Create("ValidationFlags", "XmlSchemaValidationFlags"),
        ]);
    private static readonly ImmutableHashSet<string> LongPropertyNames = ImmutableHashSet.Create("MaxCharactersInDocument", "MaxCharactersFromEntities");

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

        var ct = context.CancellationToken;

        if (EnumPropertyTypeNames.ContainsKey(propertyName))
        {
            AnalyzeEnumProperty(context, assignment, access, propertyName, ct);
        }
        else if (LongPropertyNames.Contains(propertyName))
        {
            AnalyzeLongProperty(context, assignment, access, propertyName, ct);
        }
    }

    private static void AnalyzeEnumProperty(SyntaxNodeAnalysisContext context, AssignmentExpressionSyntax assignment, MemberAccessExpressionSyntax access, string propertyName, System.Threading.CancellationToken ct)
    {
        if (assignment.Right is not CastExpressionSyntax { Expression: var innerExpr }) return;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (!IsRealXmlReaderSettingsProperty(context, access, propertyName, ct)) return;
        var enumTypeName = context.SemanticModel.GetTypeInfo(assignment.Right, ct).Type?.Name;
        if (enumTypeName != EnumPropertyTypeNames[propertyName]) return;

        var settings = new XmlReaderSettings();
        try
        {
            switch (propertyName)
            {
                case "DtdProcessing": settings.DtdProcessing = (DtdProcessing)rawValue; break;
                case "ValidationType": settings.ValidationType = (ValidationType)rawValue; break;
                default: settings.ValidationFlags = (System.Xml.Schema.XmlSchemaValidationFlags)rawValue; break;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, $"({enumTypeName}){rawValue}", $"value is outside the valid range for {enumTypeName}"));
        }
    }

    private static void AnalyzeLongProperty(SyntaxNodeAnalysisContext context, AssignmentExpressionSyntax assignment, MemberAccessExpressionSyntax access, string propertyName, System.Threading.CancellationToken ct)
    {
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not (int or long)) return;
        var value = System.Convert.ToInt64(context.SemanticModel.GetConstantValue(assignment.Right, ct).Value);
        if (value >= 0) return;

        if (!IsRealXmlReaderSettingsProperty(context, access, propertyName, ct)) return;

        var settings = new XmlReaderSettings();
        try
        {
            if (propertyName == "MaxCharactersInDocument") settings.MaxCharactersInDocument = value;
            else settings.MaxCharactersFromEntities = value;
        }
        catch (ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, value, $"{propertyName} cannot be negative"));
        }
    }

    private static bool IsRealXmlReaderSettingsProperty(SyntaxNodeAnalysisContext context, MemberAccessExpressionSyntax access, string propertyName, System.Threading.CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return false;
        if (symbolName != propertyName) return false;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        return typeName == "System.Xml.XmlReaderSettings";
    }
}
