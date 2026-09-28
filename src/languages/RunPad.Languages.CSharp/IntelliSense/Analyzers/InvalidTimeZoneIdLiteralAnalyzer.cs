using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста восемьдесят восьмой проход, batch) — own pick, not a Roslynator port; found by direct
// brainstorming after the research subagent's list (проход 377) was fully exhausted, same brainstorming
// pass that also found RPAD0379/Cookie. Same "run the real validator" theme, applied to
// `TimeZoneInfo.FindSystemTimeZoneById(string)` (a single overload — confirmed via reflection). Same
// documented machine/environment-dependency caveat this catalog already accepts for
// InvalidCultureNameLiteralAnalyzer/InvalidRegionInfoLiteralAnalyzer (RPAD0248/RPAD0372): which IDs resolve
// depends on the OS's installed tzdata/ICU database, so this analyzer's verdict reflects the ANALYSIS
// machine, not necessarily every machine the compiled script might later run on — not a new category of
// risk for this catalog, just the same accepted tradeoff applied to a new BCL surface. To keep the "not
// flagged" side of this analyzer robust across any reasonably current platform, only well-known, decades-
// stable zone IDs are used as "valid" test literals ("UTC", IANA "America/New_York", Windows "Eastern
// Standard Time" — confirmed empirically that .NET's cross-platform ID compatibility mapping, introduced
// so either convention resolves on either OS, accepts both forms here on this Linux analysis machine).
// Confirmed empirically this throws exactly `TimeZoneNotFoundException` for an unrecognized ID string —
// deliberately not catching the separate, rarer `InvalidTimeZoneException` (a corrupt-but-ID-recognized
// system entry), since that failure mode isn't something a bad ID literal can trigger.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTimeZoneIdLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0380";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TimeZoneInfo id literal is not a recognized time zone on this system",
        messageFormat: "'TimeZoneInfo.FindSystemTimeZoneById(...)' always throws TimeZoneNotFoundException at runtime — '{0}' is not a recognized time zone id on this system",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TimeZoneInfo.FindSystemTimeZoneById(string) throws TimeZoneNotFoundException for an id not present in the local system's time zone database.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FindSystemTimeZoneById" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string id) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "FindSystemTimeZoneById",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.TimeZoneInfo") return;

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), id));
        }
    }
}
