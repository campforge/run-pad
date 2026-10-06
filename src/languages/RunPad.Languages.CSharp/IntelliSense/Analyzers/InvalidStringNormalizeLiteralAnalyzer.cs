using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (432-й проход, batch) — candidate #3 from the fifth research subagent survey, re-verified
// independently before implementing. `string.Normalize()`/`.Normalize(NormalizationForm)`/
// `.IsNormalized()`/`.IsNormalized(NormalizationForm)` — the first analyzer in this catalog where the
// LITERAL itself is the RECEIVER of the call (`"literal".Normalize()`), not an argument passed to some
// other method — every other analyzer here matches a literal in an argument position. Confirmed
// empirically, matching the agent's report exactly: all four overloads throw `ArgumentException` ("String
// contains invalid Unicode code points.") for a string containing an unpaired UTF-16 surrogate (a lone
// high surrogate, a lone low surrogate, two high surrogates in a row, or a wrong-order pair), regardless
// of position (start/middle/end) or which `NormalizationForm` is requested.
//
// ENVIRONMENT-DEPENDENCE CAVEAT, confirmed empirically per this session's standing culture/environment-
// dependency documentation discipline (same spirit as RegionInfo/CultureInfo/TimeZoneInfo's caveats):
// under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, `Normalize`/`IsNormalized` become no-ops and do NOT
// throw for the same malformed literals — confirmed via a side-by-side scratch test with and without the
// env var. Confirmed via repo-wide grep that no `.csproj`/`.props`/`.targets` file in this solution (nor
// the throwaway single-file MSBuild projects `MsBuildProcessCompiler` generates for user scripts) sets
// `&lt;InvariantGlobalization&gt;`, so this is safe under this project's actual build configuration today —
// but it's a genuine, real environment-dependent behavior difference worth flagging explicitly rather than
// silently assuming away, should that ever change.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringNormalizeLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0422";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "String literal receiver of Normalize/IsNormalized contains an unpaired surrogate",
        messageFormat: "'...{0}(...)' always throws ArgumentException at runtime — this literal contains an unpaired UTF-16 surrogate character",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.Normalize/IsNormalized throw ArgumentException when the string contains an unpaired UTF-16 surrogate. Under DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, both methods become no-ops instead and never throw.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Normalize" or "IsNormalized",
            } access)
        {
            return;
        }
        if (invocation.ArgumentList.Arguments.Count > 1) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(access.Expression, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: false,
                ContainingType.SpecialType: SpecialType.System_String,
            } method)
        {
            return;
        }
        if (method.Name is not ("Normalize" or "IsNormalized")) return;

        try
        {
            if (method.Name == "Normalize")
            {
                _ = text.Normalize();
            }
            else
            {
                _ = text.IsNormalized();
            }
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), method.Name));
        }
    }
}
