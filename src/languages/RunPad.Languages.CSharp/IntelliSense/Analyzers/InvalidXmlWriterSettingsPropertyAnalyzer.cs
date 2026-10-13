using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Xml.XmlWriterSettings` — 3 enum property setters, all on a
// throwaway `new XmlWriterSettings()`: `ConformanceLevel`, `NewLineHandling`, and `NamespaceHandling` all
// throw `ArgumentOutOfRangeException` for a value outside their own enum's defined range. Confirmed
// `NamespaceHandling` specifically — despite being a `[Flags]` enum — only accepts 0 or 1 (`Default`/
// `OmitDuplicates`), confirmed empirically via the real exception message ("value ('99') must be less
// than or equal to '1'") rather than assumed from the `[Flags]` attribute alone. Confirmed invariant
// across en-US/de-DE/ar-SA/ja-JP (pure enum-range validation). No side effects. Scoped to a literal
// numeric value cast to the target enum type, same shape already established by
// `InvalidDateTimeFormatInfoEnumPropertyAnalyzer` (RPAD0442).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlWriterSettingsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0444";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlWriterSettings enum property assignment is out of range",
        messageFormat: "'XmlWriterSettings.{0} = ({1}){2}' always throws ArgumentOutOfRangeException at runtime — value is outside the valid range for {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlWriterSettings.ConformanceLevel/NewLineHandling/NamespaceHandling throw ArgumentOutOfRangeException for a value outside their own enum's defined range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> PropertyNames = ImmutableHashSet.Create(
        "ConformanceLevel", "NewLineHandling", "NamespaceHandling");

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
        if (!PropertyNames.Contains(propertyName)) return;
        if (assignment.Right is not CastExpressionSyntax { Expression: var innerExpr }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Xml.XmlWriterSettings") return;

        var enumTypeName = context.SemanticModel.GetTypeInfo(assignment.Right, ct).Type?.Name;
        if (enumTypeName != propertyName) return;

        if (!AlwaysThrows(propertyName, rawValue)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, enumTypeName, rawValue));
    }

    private static bool AlwaysThrows(string propertyName, int rawValue)
    {
        var settings = new XmlWriterSettings();
        try
        {
            switch (propertyName)
            {
                case "ConformanceLevel": settings.ConformanceLevel = (ConformanceLevel)rawValue; break;
                case "NewLineHandling": settings.NewLineHandling = (NewLineHandling)rawValue; break;
                case "NamespaceHandling": settings.NamespaceHandling = (NamespaceHandling)rawValue; break;
            }
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
