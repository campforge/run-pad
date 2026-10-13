using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; third candidate
// from the second research subagent survey (проход 397, read-only tools, doc-based hunches only). The
// agent's doc-reading guess for this one — that out-of-range date/time components (e.g. month 13, hour 25)
// throw a SEPARATE `ArgumentOutOfRangeException` alongside `FormatException` for pure grammar errors — was
// CONFIRMED WRONG by empirical re-verification: every malformed literal tried, including out-of-range
// month/day/hour values, throws plain `FormatException`. This is exactly why this session always
// re-verifies doc-based hunches empirically rather than trusting them, and this candidate is the concrete
// example of that discipline catching a wrong guess before it shipped. Scoped to the single-string overload
// only (confirmed via reflection: `XmlConvert` has three `ToDateTimeOffset` overloads — this one, plus
// `(string, string)` and `(string, string[])` format-specifier variants, deliberately out of scope, same
// restraint as this catalog's other single-arg-overload analyzers). Thematically adjacent to, but genuinely
// NOT overlapping with, `DateTimeOffset.Parse` coverage in InvalidDateOrTimeParseLiteralAnalyzer (RPAD0365,
// проход 372) — confirmed empirically `XmlConvert.ToDateTimeOffset` requires strict ISO 8601/XSD dateTime
// grammar with no culture-based fallback whatsoever (rejects "1/1/2024" and "January 1, 2024" outright,
// both of which regular `DateTimeOffset.Parse` accepts), same class of legitimate coexistence already
// established for ContentType/MediaTypeHeaderValue, DateTime.Parse/SqlDateTime.Parse (RPAD0388), and
// TimeSpan.Parse/XmlConvert.ToTimeSpan (RPAD0390) in this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlDateTimeOffsetLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0391";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.ToDateTimeOffset argument is not a valid XSD dateTime literal",
        messageFormat: "'XmlConvert.ToDateTimeOffset(...)' always throws FormatException at runtime — '{0}' is not a valid XSD dateTime literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.ToDateTimeOffset(string) throws FormatException for a string that isn't a valid ISO 8601/XSD dateTime — it has no culture-based fallback, unlike DateTimeOffset.Parse.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToDateTimeOffset" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToDateTimeOffset",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.XmlConvert") return;

        try
        {
            _ = XmlConvert.ToDateTimeOffset(value);
        }
        catch (System.FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
        }
    }
}
