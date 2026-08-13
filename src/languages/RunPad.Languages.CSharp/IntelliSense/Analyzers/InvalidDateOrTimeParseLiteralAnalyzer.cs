using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста семьдесят второй проход, batch) — own pick, not a Roslynator port. Sibling of
// InvalidNumericParseFormatAnalyzer (RPAD0356) — same "run the real validator, but only after a
// conservative structural guard" theme, applied to `DateTime`/`DateTimeOffset`/`TimeSpan`/`DateOnly`/
// `TimeOnly`.Parse(string) instead: a literal that can't possibly be a date/time in ANY format/culture
// always throws `FormatException`. `InvalidDateTimeStylesCombinationAnalyzer`'s (RPAD0276, проход 288)
// own doc comment explicitly named this exact question — "the actual date string" being unparseable —
// as "a different, string-content-dependent issue this project has deliberately not tried to validate
// further"; this analyzer is that follow-up. Deliberately does NOT reuse RPAD0356's "contains a Unicode
// letter" guard — that direction is backwards here, since REAL dates routinely contain letters (month
// names, day names, "AD"/"AM"/"PM", timezone abbreviations) in many valid formats. Instead uses the
// OPPOSITE, still-conservative guard: a literal containing NO ASCII digit character at all can't be a
// valid date/time in any format, confirmed empirically (scratch `dotnet run` script) across six
// culturally-diverse `CurrentCulture` settings (en-US/de-DE/ar-SA/th-TH/he-IL/ja-JP, the last three
// specifically for non-Gregorian-calendar or non-Latin-script locales) — even plausible-looking bare
// tokens with no digits at all (`"January"`, `"Monday"`, `"now"`, `"today"`, `"AM"`/`"PM"`, `"UTC"`/
// `"GMT"`, `"Z"`) all still throw, since every one of .NET's recognized date/time formats needs at least
// one digit somewhere (day, month-as-number, or year). This single precondition also subsumes the empty-
// and whitespace-only-string cases without a separate check. The actual verdict still comes from calling
// the REAL `TargetType.TryParse` (with `CultureInfo.InvariantCulture` explicitly, not the ambient
// culture) rather than hand-reasoning further about which no-digit tokens might be exceptions — same
// discipline RPAD0356 already established, reused here for the same reason.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDateOrTimeParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0365";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Date/time Parse argument cannot possibly be a valid date or time",
        messageFormat: "'{0}' always throws FormatException at runtime — \"{1}\" is not a valid {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DateTime/DateTimeOffset/TimeSpan/DateOnly/TimeOnly.Parse(string) throws FormatException when the literal contains no digit character at all, since every recognized format needs at least one.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetTypes = ImmutableHashSet.Create(
        "System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.DateOnly", "System.TimeOnly");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var literalArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(literalArg, ct).Value is not string literal) return;
        if (literal.Any(c => c is >= '0' and <= '9')) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Parse",
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        var typeName = containingType.ToDisplayString();
        if (!TargetTypes.Contains(typeName)) return;

        if (!FailsToParse(typeName, literal)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), literal, containingType.Name));
    }

    private static bool FailsToParse(string typeName, string literal) => typeName switch
    {
        "System.DateTime" => !System.DateTime.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "System.DateTimeOffset" => !System.DateTimeOffset.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "System.TimeSpan" => !System.TimeSpan.TryParse(literal, CultureInfo.InvariantCulture, out _),
        "System.DateOnly" => !System.DateOnly.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "System.TimeOnly" => !System.TimeOnly.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => false,
    };
}
