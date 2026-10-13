using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the thirty-ninth research subagent survey,
// re-verified independently before implementing. `HijriCalendar.HijriAdjustment` (int, get/set) — legal
// range is exactly [-2, 2]; any other value always throws `ArgumentOutOfRangeException`
// (`ParamName` = "value"), regardless of any other program state. Confirmed culture-invariant (tested
// ar-SA — identical exception type/ParamName; message text embeds an RTL mark for the negative bound
// under ar-SA, but that's display-only). `UmAlQuraCalendar` derives directly from `Calendar` (not
// `HijriCalendar`) and has no equivalent property, so this is deliberately scoped to the receiver's
// compile-time type being exactly `System.Globalization.HijriCalendar`. Matches by the ASSIGNMENT
// receiver's compile-time type, same precedent as `InvalidCalendarTwoDigitYearMaxAnalyzer` (RPAD0518) —
// a `HijriCalendar cal = new(); cal.HijriAdjustment = 3;` shape is caught just as well as an inline
// object-creation receiver.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHijriCalendarAdjustmentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0519";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "HijriCalendar.HijriAdjustment assignment is out of range",
        messageFormat: "'HijriCalendar.HijriAdjustment = {0}' always throws ArgumentOutOfRangeException at runtime — value must be between -2 and 2",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "HijriCalendar.HijriAdjustment throws ArgumentOutOfRangeException for any value outside [-2, 2].");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "HijriAdjustment" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "HijriAdjustment" })
            return;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int adjustment) return;

        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Globalization.HijriCalendar") return;

        try
        {
            _ = new HijriCalendar { HijriAdjustment = adjustment };
        }
        catch (ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), adjustment));
        }
    }
}
