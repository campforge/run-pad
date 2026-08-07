using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести семьдесят четвёртый проход, batch) — own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidGuidLiteralFormatAnalyzer/InvalidIPAddressLiteralAnalyzer
// (RPAD0246, RPAD0249), but on a CONSTRUCTOR instead of a Parse method: `new DateTime(year, month,
// day[, hour, minute, second[, millisecond]])` throws ArgumentOutOfRangeException for a
// year/month/day/hour/minute/second/millisecond combination that isn't a valid calendar date/time —
// e.g. `new DateTime(2023, 13, 1)` (month 13) or `new DateTime(2023, 2, 29)` (not a leap year). Unlike
// DateTime.Parse (CultureSensitiveNumericParseAnalyzer, RPAD0098), this constructor takes plain `int`
// arguments, not a culture-formatted string — confirmed empirically across five culturally-diverse
// CurrentCulture settings (en-US/de-DE/ar-SA/th-TH/he-IL, the last two specifically chosen for having
// non-Gregorian native calendars) that the plain int-only constructor always validates against the
// Gregorian calendar regardless of thread culture, so there's no cross-environment risk. Scoped to the
// all-`int`-parameter overloads by checking the RESOLVED constructor symbol's parameter types, not just
// argument count — this is what correctly distinguishes the 7-arg `(y,m,d,h,mi,s,millisecond)` overload
// from the 7-arg `(y,m,d,h,mi,s,DateTimeKind)` overload, which share an argument count but not a
// parameter type in the last position; only proceeds when every argument is itself a literal (constant)
// `int`, then calls the REAL constructor in-process on those values inside a try/catch, reporting only
// if that call itself throws.
// Extended (двести семьдесят пятый проход, batch) to also cover `DateOnly(year, month, day)` and
// `TimeOnly(hour, minute[, second[, millisecond]])` (.NET 6+, within this project's SDK floor) — the
// same int-only, culture-invariant validation, confirmed empirically for both before extending.
// Extended again (триста сорок четвёртый проход, batch) to also cover `TimeOnly`'s 5-argument
// `(hour, minute, second, millisecond, microsecond)` overload — confirmed empirically to throw
// identically to the other TimeOnly overloads; simply widened the accepted argument count and added a
// branch to `AlwaysThrows`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDateTimeConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0264";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTime/DateOnly/TimeOnly constructor arguments do not form a valid date/time",
        messageFormat: "'new {0}(...)' always throws ArgumentOutOfRangeException at runtime — {1} is not a valid argument combination",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The int-only DateTime/DateOnly/TimeOnly constructor overloads throw ArgumentOutOfRangeException when the year/month/day/hour/minute/second/millisecond arguments don't form a valid calendar date/time.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { } argumentList) return;
        var argCount = argumentList.Arguments.Count;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;

        var typeKeyword = ctor.ContainingType?.ToDisplayString() switch
        {
            "System.DateTime" when argCount is 3 or 6 or 7 => "DateTime",
            "System.DateOnly" when argCount is 3 => "DateOnly",
            "System.TimeOnly" when argCount is 2 or 3 or 4 or 5 => "TimeOnly",
            _ => null,
        };
        if (typeKeyword is null) return;
        if (!ctor.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32)) return;

        var values = new int[argCount];
        for (var i = 0; i < argCount; i++)
        {
            if (context.SemanticModel.GetConstantValue(argumentList.Arguments[i].Expression, ct).Value is not int value)
                return;
            values[i] = value;
        }

        if (!AlwaysThrows(typeKeyword, values)) return;

        var display = string.Join(", ", values);
        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), typeKeyword, display));
    }

    private static bool AlwaysThrows(string typeKeyword, int[] v)
    {
        try
        {
            switch (typeKeyword)
            {
                case "DateTime":
                    if (v.Length == 3) _ = new DateTime(v[0], v[1], v[2]);
                    else if (v.Length == 6) _ = new DateTime(v[0], v[1], v[2], v[3], v[4], v[5]);
                    else _ = new DateTime(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
                    break;
                case "DateOnly":
                    _ = new DateOnly(v[0], v[1], v[2]);
                    break;
                default:
                    if (v.Length == 2) _ = new TimeOnly(v[0], v[1]);
                    else if (v.Length == 3) _ = new TimeOnly(v[0], v[1], v[2]);
                    else if (v.Length == 4) _ = new TimeOnly(v[0], v[1], v[2], v[3]);
                    else _ = new TimeOnly(v[0], v[1], v[2], v[3], v[4]);
                    break;
            }
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
