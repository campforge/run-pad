using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidRegexOptionsCombinationAnalyzer/InvalidNumberStylesCombinationAnalyzer
// (RPAD0274/RPAD0275, проходы 286-287), applied to a third flags enum: `DateTime.Parse(s, provider,
// DateTimeStyles styles)` throws ArgumentException when `styles` combines
// `DateTimeStyles.AssumeLocal`/`AssumeUniversal`/`RoundtripKind` with each other (any two of the three
// together — confirmed empirically before writing this). This is a PURE styles-combination check,
// independent of the actual date string or culture — confirmed by testing every invalid combination
// against the SAME valid ISO date string, where only the styles value determined the outcome, unlike
// DateTime.Parse's single-argument overload (CultureSensitiveNumericParseAnalyzer, RPAD0098), whose
// concern is the FORMAT being ambiguous across cultures, a different, string-content-dependent issue this
// project has deliberately not tried to validate further. Calls the REAL `DateTime.TryParse` (not
// `Parse`, same EmptyCatchClauseAnalyzer/RPAD0001 self-trigger avoidance as RPAD0275) on a fixed,
// always-valid ISO 8601 placeholder string with `CultureInfo.InvariantCulture`, reporting only if that
// call itself throws ArgumentException. Extended in the двести девяносто третий проход to also cover
// `DateTimeOffset.Parse(s, provider, DateTimeStyles)` — confirmed empirically to throw identically for
// the same styles combination. `DateTimeOffset` isn't a Roslyn `SpecialType` (unlike `DateTime`), so it's
// matched by name/namespace and dispatched to the real `DateTimeOffset.TryParse`, same per-type dispatch
// discipline as InvalidDateTimeConstructorArgumentAnalyzer (RPAD0264)/InvalidNumberStylesCombinationAnalyzer
// (RPAD0275).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDateTimeStylesCombinationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0276";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DateTimeStyles combination always throws",
        messageFormat: "'{0}.Parse(...)' always throws ArgumentException at runtime — this DateTimeStyles combination is not valid together",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DateTimeStyles.AssumeLocal, AssumeUniversal, and RoundtripKind are mutually exclusive — combining any two of them throws ArgumentException.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, _, { Expression: var stylesArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(stylesArg, ct).Value is not int stylesValue) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.Parameters is not [_, _, { Type.Name: "DateTimeStyles" }]) return;

        string displayName;
        if (method.ContainingType?.SpecialType == SpecialType.System_DateTime)
        {
            displayName = "DateTime";
        }
        else if (method.ContainingType is { Name: "DateTimeOffset" } dtoType
            && dtoType.ContainingNamespace?.ToDisplayString() == "System")
        {
            displayName = "DateTimeOffset";
        }
        else
        {
            return;
        }

        if (!AlwaysThrows(displayName, (DateTimeStyles)stylesValue)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), displayName));
    }

    private static bool AlwaysThrows(string displayName, DateTimeStyles styles)
    {
        try
        {
            // The return value itself (true/false) is irrelevant here — only whether the call THROWS
            // indicates an invalid styles combination; a `false` return would just mean the placeholder
            // string failed to parse, which says nothing about styles validity and must not be
            // misreported as "always throws".
            const string placeholder = "2024-01-01T00:00:00Z";
            if (displayName == "DateTime")
                DateTime.TryParse(placeholder, CultureInfo.InvariantCulture, styles, out _);
            else
                DateTimeOffset.TryParse(placeholder, CultureInfo.InvariantCulture, styles, out _);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
