using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; found by direct brainstorming
// while re-checking InvalidNumericParseFormatAnalyzer's (RPAD0356) Convert.ToXxx coverage list (11 numeric
// conversions) — `Convert.ToDateTime(string)` is NOT among them and has no analyzer of its
// own. Sibling of InvalidDateOrTimeParseLiteralAnalyzer (RPAD0365), reusing that analyzer's own
// conservative guard: a literal containing no ASCII digit character at all can't be a valid date/time in
// any format, since every one of .NET's recognized formats needs at least one digit (day, month-as-number,
// or year) — confirmed empirically for `Convert.ToDateTime` specifically (not just inherited from the
// `Parse` sibling) across the same six culturally-diverse `CurrentCulture` settings RPAD0365 used (en-US/
// de-DE/ar-SA/th-TH/he-IL/ja-JP), since `Convert.ToDateTime(string)` — unlike `DateTime.Parse` — parses
// using the AMBIENT current culture internally, not an explicit provider, making this guard's
// culture-invariance the entire reason this analyzer is safe at all. Confirmed `Convert.ToDateTime(null)`
// is a special case that does NOT throw (returns `DateTime.MinValue`, an established `Convert.ToXxx`
// null-handling convention) — harmless here since a `null` constant never matches this analyzer's `is not
// string` literal-extraction check in the first place. The actual verdict still comes from calling the
// REAL `DateTime.TryParse` (with `CultureInfo.InvariantCulture` explicitly) rather than reasoning further
// about which no-digit tokens might be exceptions, same discipline RPAD0365 already established.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidConvertToDateTimeLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0433";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.ToDateTime argument cannot possibly be a valid date or time",
        messageFormat: "'Convert.ToDateTime(...)' always throws FormatException at runtime — \"{0}\" is not a valid DateTime",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.ToDateTime(string) throws FormatException when the literal contains no digit character at all, since every recognized format needs at least one.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToDateTime" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var literalArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(literalArg, ct).Value is not string literal) return;
        if (literal.Any(c => c is >= '0' and <= '9')) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToDateTime",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Convert") return;

        if (System.DateTime.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), literal));
    }
}
