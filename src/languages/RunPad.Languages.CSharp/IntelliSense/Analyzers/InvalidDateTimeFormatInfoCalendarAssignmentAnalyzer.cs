using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (450-й проход, batch) — candidate #1 from the twelfth research subagent survey, re-verified
// independently before implementing. `DateTimeFormatInfo.Calendar` setter — assigning an incompatible
// `Calendar` subtype throws `ArgumentOutOfRangeException` ("Not a valid calendar for the given culture.").
// Confirmed empirically: 13 of the 14 public concrete `Calendar` subtypes in the BCL always throw
// (HijriCalendar, HebrewCalendar, JapaneseCalendar, PersianCalendar, ThaiBuddhistCalendar,
// UmAlQuraCalendar, ChineseLunisolarCalendar, JapaneseLunisolarCalendar, JulianCalendar, KoreanCalendar,
// KoreanLunisolarCalendar, TaiwanCalendar, TaiwanLunisolarCalendar) — only a plain `new GregorianCalendar()`
// (or explicit `GregorianCalendarTypes.Localized`) is ever valid; every OTHER `GregorianCalendarTypes`
// value (e.g. `USEnglish`) also throws, confirmed empirically rather than assumed. Confirmed invariant
// across en-US/de-DE/ar-SA/ja-JP — this relies on `new DateTimeFormatInfo()` always being culture-neutral
// by construction (the same mechanism `InvalidNumberFormatInfoPatternAnalyzer`, RPAD0439, already relies
// on for its own culture-invariance), never on the ambient `CultureInfo.CurrentCulture`. Scoped to a
// literal `new CalendarSubtype()` (or `new GregorianCalendar(nonLocalizedType)`) object-creation
// expression as the assignment's right-hand side — the only shape the real validator can be safely probed
// against without needing the actual runtime `Calendar` instance the user's expression would otherwise
// produce.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDateTimeFormatInfoCalendarAssignmentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0441";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTimeFormatInfo.Calendar assignment is not a supported calendar",
        messageFormat: "'DateTimeFormatInfo.Calendar = new {0}(...)' always throws ArgumentOutOfRangeException at runtime — not a valid calendar for DateTimeFormatInfo",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DateTimeFormatInfo.Calendar throws ArgumentOutOfRangeException for any Calendar subtype other than a (default/Localized) GregorianCalendar.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> AlwaysInvalidCalendarTypes = ImmutableHashSet.Create(
        "System.Globalization.ChineseLunisolarCalendar", "System.Globalization.HebrewCalendar",
        "System.Globalization.HijriCalendar", "System.Globalization.JapaneseCalendar",
        "System.Globalization.JapaneseLunisolarCalendar", "System.Globalization.JulianCalendar",
        "System.Globalization.KoreanCalendar", "System.Globalization.KoreanLunisolarCalendar",
        "System.Globalization.PersianCalendar", "System.Globalization.TaiwanCalendar",
        "System.Globalization.TaiwanLunisolarCalendar", "System.Globalization.ThaiBuddhistCalendar",
        "System.Globalization.UmAlQuraCalendar");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Calendar" } access)
            return;
        if (assignment.Right is not BaseObjectCreationExpressionSyntax creation) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "Calendar" } property)
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Globalization.DateTimeFormatInfo") return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } calendarType })
            return;
        var calendarTypeName = calendarType.ToDisplayString();

        Calendar? calendar = calendarTypeName switch
        {
            "System.Globalization.GregorianCalendar" => ClassifyGregorian(creation, context.SemanticModel, ct),
            _ when AlwaysInvalidCalendarTypes.Contains(calendarTypeName) => TryCreate(calendarTypeName),
            _ => null,
        };
        if (calendar is null) return;

        try
        {
            _ = new DateTimeFormatInfo { Calendar = calendar };
        }
        catch (ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), calendarType.Name));
        }
    }

    private static Calendar? ClassifyGregorian(BaseObjectCreationExpressionSyntax creation, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (creation.ArgumentList is not { Arguments: [{ Expression: var typeArg }] }) return null;
        if (model.GetConstantValue(typeArg, ct).Value is not int rawType) return null;
        var type = (GregorianCalendarTypes)rawType;
        return type == GregorianCalendarTypes.Localized ? null : new GregorianCalendar(type);
    }

    private static Calendar? TryCreate(string typeName)
    {
        try
        {
            return typeName switch
            {
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
            // concern) must not be misreported as a Calendar-assignment failure.
            return null;
        }
    }
}
