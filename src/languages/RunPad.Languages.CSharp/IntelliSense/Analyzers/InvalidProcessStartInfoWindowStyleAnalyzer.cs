using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the fourteenth research subagent survey,
// re-verified independently before implementing. `ProcessStartInfo.WindowStyle` — assigning a
// `ProcessWindowStyle` value outside its 0-3 defined range (`Normal`/`Hidden`/`Minimized`/`Maximized`)
// throws `System.ComponentModel.InvalidEnumArgumentException` (a genuinely different exception TYPE from
// the plain `ArgumentOutOfRangeException` this catalog's other enum-range analyzers throw — confirmed
// empirically, not assumed, since .NET's own enum-range validators are inconsistent about which of the
// two they use). Confirmed empirically this is a pure data-holder property with ZERO side effects —
// assigning it never starts, queries, or otherwise touches any OS process; confirmed by setting it
// repeatedly on a throwaway `ProcessStartInfo` with no process ever started. Confirmed invariant across
// en-US/de-DE/ar-SA/ja-JP. Scoped to a literal numeric value cast to `ProcessWindowStyle`, same shape
// already established by `InvalidDateTimeFormatInfoEnumPropertyAnalyzer` (RPAD0442)/
// `InvalidXmlWriterSettingsPropertyAnalyzer` (RPAD0444).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidProcessStartInfoWindowStyleAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0450";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ProcessStartInfo.WindowStyle assignment is out of range",
        messageFormat: "'ProcessStartInfo.WindowStyle = (ProcessWindowStyle){0}' always throws InvalidEnumArgumentException at runtime — value is outside the valid range for ProcessWindowStyle",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ProcessStartInfo.WindowStyle throws System.ComponentModel.InvalidEnumArgumentException for a value outside ProcessWindowStyle's 0-3 defined range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "WindowStyle" } access)
            return;
        if (assignment.Right is not CastExpressionSyntax { Expression: var innerExpr }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "WindowStyle" })
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Diagnostics.ProcessStartInfo") return;

        var enumTypeName = context.SemanticModel.GetTypeInfo(assignment.Right, ct).Type?.ToDisplayString();
        if (enumTypeName != "System.Diagnostics.ProcessWindowStyle") return;

        try
        {
            _ = new ProcessStartInfo { WindowStyle = (ProcessWindowStyle)rawValue };
        }
        catch (System.ComponentModel.InvalidEnumArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), rawValue));
        }
    }
}
