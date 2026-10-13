using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the sixth research subagent survey (Tier 2, "needs
// a culture-safety guard before shipping"), re-verified independently before implementing.
// `DateTime/DateTimeOffset/TimeSpan/DateOnly/TimeOnly.ParseExact(string, string, IFormatProvider)` — the
// `ParseExact` sibling of the already-covered plain `Parse` (RPAD0365, проход 372), whose own doc comment
// explicitly notes it only matches the 1-arg `Parse` overload, never `ParseExact`. Confirmed via grep no
// existing analyzer even mentions `ParseExact` for these five types (only `Guid.ParseExact`, RPAD0252-ish,
// is covered, a culture-free format entirely).
//
// CULTURE-SAFETY SCOPING, confirmed empirically per the agent's explicit warning: a format string
// containing a name-based specifier (`MMMM`, `ddd`, etc.) can parse successfully under one culture and
// fail under another for the SAME date-string literal — confirmed directly: `DateTime.ParseExact("Januar
// 01 2024", "MMMM dd yyyy", CultureInfo.InvariantCulture)` throws (not an English month name), but the
// identical call with `CultureInfo.GetCultureInfo("de-DE")` instead SUCCEEDS (valid German). This analyzer
// avoids that entire risk class by requiring the THIRD argument to be a provable, literal reference to
// `CultureInfo.InvariantCulture` specifically (checked via symbol resolution, not just any expression
// that happens to evaluate to it) — when that's true, running the real validator WITH
// `CultureInfo.InvariantCulture` exactly reproduces what that specific call site does at runtime, with no
// possibility of a different culture at the real call site disagreeing with the verdict. Any other
// provider expression (a variable, `CultureInfo.CurrentCulture`, a different named culture, `null`) is
// left unanalyzed entirely, deliberately erring toward false negatives over false positives here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDateOrTimeParseExactLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0427";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Date/time ParseExact arguments do not match under InvariantCulture",
        messageFormat: "'{0}' always throws FormatException at runtime — \"{1}\" does not match the format \"{2}\" under CultureInfo.InvariantCulture",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DateTime/DateTimeOffset/TimeSpan/DateOnly/TimeOnly.ParseExact(string, string, IFormatProvider) throws FormatException when the date string doesn't match the format exactly. Scoped to call sites that pass CultureInfo.InvariantCulture literally, since ParseExact's outcome for name-based format specifiers (MMMM, ddd, etc.) depends on which culture is passed.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ParseExact" })
            return;
        // DateTime/DateTimeOffset/TimeSpan have a 3-arg (string, string, IFormatProvider) overload;
        // DateOnly/TimeOnly do not — their string-based ParseExact is only 2-arg or the 4-arg
        // (string, string, IFormatProvider, DateTimeStyles/TimeSpanStyles) form, confirmed via reflection.
        // Both arities are matched here, always reading the date/format/provider from positions 0/1/2 and
        // ignoring an optional trailing styles argument.
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count is not (3 or 4)) return;
        var dateArg = arguments[0].Expression;
        var formatArg = arguments[1].Expression;
        var providerArg = arguments[2].Expression;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(dateArg, ct).Value is not string dateText) return;
        if (context.SemanticModel.GetConstantValue(formatArg, ct).Value is not string format) return;

        if (context.SemanticModel.GetSymbolInfo(providerArg, ct).Symbol is not { Name: "InvariantCulture" } providerSymbol) return;
        if (providerSymbol.ContainingType?.ToDisplayString() != "System.Globalization.CultureInfo") return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ParseExact",
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_String }, { Type.Name: "IFormatProvider" }, ..],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        var typeName = containingType.ToDisplayString();
        if (!TargetTypes.Contains(typeName)) return;

        if (!FailsToParseExact(typeName, dateText, format)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), dateText, format));
    }

    private static bool FailsToParseExact(string typeName, string dateText, string format) => typeName switch
    {
        "System.DateTime" => !System.DateTime.TryParseExact(dateText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "System.DateTimeOffset" => !System.DateTimeOffset.TryParseExact(dateText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "System.TimeSpan" => !System.TimeSpan.TryParseExact(dateText, format, CultureInfo.InvariantCulture, out _),
        "System.DateOnly" => !System.DateOnly.TryParseExact(dateText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "System.TimeOnly" => !System.TimeOnly.TryParseExact(dateText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => false,
    };
}
