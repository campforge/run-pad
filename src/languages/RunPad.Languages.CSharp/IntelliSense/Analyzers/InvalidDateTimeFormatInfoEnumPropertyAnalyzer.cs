using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidates #2/#3 from the twelfth research subagent survey,
// re-verified independently before implementing. `DateTimeFormatInfo.FirstDayOfWeek` (valid range
// `DayOfWeek.Sunday`(0)-`Saturday`(6)) and `DateTimeFormatInfo.CalendarWeekRule` (valid range
// `CalendarWeekRule.FirstDay`(0)-`FirstFourDayWeek`(2)) both throw `ArgumentOutOfRangeException` for a
// value outside their own enum's defined range — confirmed empirically for out-of-range casts on both
// sides (negative and too-large) and confirmed the in-range boundary values do NOT throw. Confirmed
// invariant across en-US/de-DE/ar-SA/ja-JP (relies on `new DateTimeFormatInfo()` always being
// culture-neutral by construction, same mechanism already established for RPAD0439/RPAD0441). Scoped to a
// literal numeric value cast to the target enum type (`(DayOfWeek)7`, `(CalendarWeekRule)(-1)`) — the only
// shape a compile-time-constant out-of-range enum value can be written in C#, since neither enum has a
// named member outside its own valid range to reference directly.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDateTimeFormatInfoEnumPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0442";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTimeFormatInfo enum property assignment is out of range",
        messageFormat: "'DateTimeFormatInfo.{0} = ({1}){2}' always throws ArgumentOutOfRangeException at runtime — value is outside the valid range for {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DateTimeFormatInfo.FirstDayOfWeek and DateTimeFormatInfo.CalendarWeekRule throw ArgumentOutOfRangeException for a value outside their own enum's defined range.");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FirstDayOfWeek" or "CalendarWeekRule" } access)
            return;
        var propertyName = access.Name.Identifier.Text;
        if (assignment.Right is not CastExpressionSyntax { Expression: var innerExpr }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Globalization.DateTimeFormatInfo") return;

        var enumTypeName = context.SemanticModel.GetTypeInfo(assignment.Right, ct).Type?.Name;
        if (enumTypeName != propertyName switch { "FirstDayOfWeek" => "DayOfWeek", "CalendarWeekRule" => "CalendarWeekRule", _ => null })
            return;

        if (!AlwaysThrows(propertyName, rawValue)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, enumTypeName, rawValue));
    }

    private static bool AlwaysThrows(string propertyName, int rawValue)
    {
        var info = new DateTimeFormatInfo();
        try
        {
            if (propertyName == "FirstDayOfWeek")
                info.FirstDayOfWeek = (DayOfWeek)rawValue;
            else
                info.CalendarWeekRule = (CalendarWeekRule)rawValue;
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
