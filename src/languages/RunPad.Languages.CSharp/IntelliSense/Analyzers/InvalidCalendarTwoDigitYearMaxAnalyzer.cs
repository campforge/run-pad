using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the thirty-ninth research subagent survey,
// re-verified independently before implementing. `Calendar.TwoDigitYearMax` (int, get/set) — every
// concrete `Calendar` subtype in the BCL enforces its OWN `[min, max]` range (min is always 99; max
// varies wildly per type — 9999 for `GregorianCalendar`/`JulianCalendar`, 9666 for `HijriCalendar`, 5999
// for `HebrewCalendar`, 1500 for `UmAlQuraCalendar`, etc.) — a value outside that specific instance's own
// range always throws `ArgumentOutOfRangeException` (`ParamName` = "value"), regardless of any other
// program state. Confirmed culture-invariant (tested ar-SA — identical exception type/ParamName, only
// message text differs). Same "run the real validator on the receiver's own concrete type" shape as
// `InvalidCryptoAlgorithmKeySizeAnalyzer` (RPAD0352) and `InvalidDateTimeFormatInfoCalendarAssignmentAnalyzer`
// (RPAD0441) — probes a real, freshly-constructed instance of the receiver's exact compile-time `Calendar`
// subtype (via each type's public parameterless constructor) rather than hand-coding 14 separate ranges,
// so the check stays correct even if a future runtime revises a type's bounds. Matches by the ASSIGNMENT
// receiver's compile-time type (not requiring the receiver to be a literal `new Calendar()` expression on
// the same line) — a `GregorianCalendar cal = new(); cal.TwoDigitYearMax = 1;` shape is caught just as
// well as an inline object-creation receiver, since the concrete calendar type alone determines the valid
// range, independent of how the instance was obtained.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCalendarTwoDigitYearMaxAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0518";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Calendar.TwoDigitYearMax assignment is out of range for this calendar",
        messageFormat: "'{0}.TwoDigitYearMax = {1}' always throws ArgumentOutOfRangeException at runtime — not a valid year for {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Calendar.TwoDigitYearMax throws ArgumentOutOfRangeException for a value outside the concrete calendar type's own [min, max] range (min is always 99; max varies per calendar type).");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "TwoDigitYearMax" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "TwoDigitYearMax" })
            return;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int year) return;

        var calendarTypeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (calendarTypeName is null) return;

        var calendar = TryCreate(calendarTypeName);
        if (calendar is null) return;

        try
        {
            calendar.TwoDigitYearMax = year;
        }
        catch (ArgumentOutOfRangeException)
        {
            var typeName = calendarTypeName[(calendarTypeName.LastIndexOf('.') + 1)..];
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), typeName, year));
        }
    }

    private static Calendar? TryCreate(string typeName)
    {
        try
        {
            return typeName switch
            {
                "System.Globalization.GregorianCalendar" => new GregorianCalendar(),
                "System.Globalization.ChineseLunisolarCalendar" => new ChineseLunisolarCalendar(),
                "System.Globalization.HebrewCalendar" => new HebrewCalendar(),
                "System.Globalization.HijriCalendar" => new HijriCalendar(),
                "System.Globalization.JapaneseCalendar" => new JapaneseCalendar(),
                "System.Globalization.JapaneseLunisolarCalendar" => new JapaneseLunisolarCalendar(),
                "System.Globalization.JulianCalendar" => new JulianCalendar(),
                "System.Globalization.KoreanCalendar" => new KoreanCalendar(),
                "System.Globalization.KoreanLunisolarCalendar" => new KoreanLunisolarCalendar(),
                "System.Globalization.PersianCalendar" => new PersianCalendar(),
                "System.Globalization.TaiwanCalendar" => new TaiwanCalendar(),
                "System.Globalization.TaiwanLunisolarCalendar" => new TaiwanLunisolarCalendar(),
                "System.Globalization.ThaiBuddhistCalendar" => new ThaiBuddhistCalendar(),
                "System.Globalization.UmAlQuraCalendar" => new UmAlQuraCalendar(),
                _ => null,
            };
        }
        catch
        {
            // A calendar's own parameterless constructor failing (unlikely, but not this analyzer's
            // concern) must not be misreported as a TwoDigitYearMax failure.
            return null;
        }
    }
}
