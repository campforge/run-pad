using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (448-й проход, batch) — candidate #1 from the eleventh research subagent survey, re-verified
// independently before implementing. `TimeZoneInfo.CreateCustomTimeZone(string id, TimeSpan baseUtcOffset,
// ...)` — all three overloads (4-arg, 6-arg with disableDaylightSavingTime, 7-arg with AdjustmentRule[])
// validate the `baseUtcOffset` parameter (consistently the 2nd positional argument) identically. Confirmed
// empirically, matching the agent's report: `ArgumentOutOfRangeException` when the offset's absolute value
// exceeds 14 hours (confirmed the boundary itself is inclusive — exactly ±14:00:00 is valid, one minute
// past either direction throws), or `ArgumentException` when the offset isn't a whole number of minutes
// (e.g. an odd-seconds `TimeSpan`). Confirmed invariant across en-US/de-DE/ar-SA/ja-JP (pure `TimeSpan`
// arithmetic, no culture dependence). No side effects — creating (and discarding) a custom `TimeZoneInfo`
// with a throwaway `id` has no observable effect, confirmed by creating several with distinct literal ids
// in a row. Reuses the same "extract a constant-foldable TimeSpan, call the real validator" technique
// already established by `InvalidCancellationTokenSourceTimeSpanDelayAnalyzer` (RPAD0280) for
// `CancellationTokenSource(TimeSpan)`/`Task.Delay(TimeSpan)` — recognizing `TimeSpan.Zero` and
// `TimeSpan.FromXxx(literalNumber)`; other forms (`new TimeSpan(...)`, arithmetic, non-literal factory
// arguments) are not evaluated, same documented MVP scoping as that analyzer.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTimeZoneCustomOffsetAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0438";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TimeZoneInfo.CreateCustomTimeZone offset always throws",
        messageFormat: "'TimeZoneInfo.CreateCustomTimeZone(...)' always throws {0} at runtime — baseUtcOffset must be within ±14:00:00 and a whole number of minutes",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TimeZoneInfo.CreateCustomTimeZone throws ArgumentOutOfRangeException for a baseUtcOffset whose absolute value exceeds 14 hours, or ArgumentException for one that isn't a whole number of minutes.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateCustomTimeZone" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var offsetArg }, ..]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                Name: "CreateCustomTimeZone",
                Parameters: [_, { Type.Name: "TimeSpan" }, ..],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.TimeZoneInfo") return;

        if (TryGetConstantTimeSpan(context.SemanticModel, offsetArg, ct) is not { } offset) return;

        var exceptionType = ClassifyFailure(offset);
        if (exceptionType is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), exceptionType));
    }

    private static string? ClassifyFailure(TimeSpan offset)
    {
        try
        {
            _ = TimeZoneInfo.CreateCustomTimeZone("RPAD0438Probe", offset, "d", "s");
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return "ArgumentOutOfRangeException";
        }
        catch (ArgumentException)
        {
            return "ArgumentException";
        }
    }

    private static TimeSpan? TryGetConstantTimeSpan(SemanticModel model, ExpressionSyntax expr, CancellationToken ct)
    {
        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "Zero" } zeroAccess
            && model.GetSymbolInfo(zeroAccess, ct).Symbol is IFieldSymbol { ContainingType: { Name: "TimeSpan" } zeroType }
            && zeroType.ContainingNamespace?.ToDisplayString() == "System")
        {
            return TimeSpan.Zero;
        }

        if (expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var factoryName }, ArgumentList.Arguments: [{ Expression: var factoryArg }] } factoryInvocation
            && model.GetSymbolInfo(factoryInvocation, ct).Symbol is IMethodSymbol { ContainingType: { Name: "TimeSpan" } factoryType }
            && factoryType.ContainingNamespace?.ToDisplayString() == "System"
            && model.GetConstantValue(factoryArg, ct) is { HasValue: true, Value: { } rawValue }
            && ToDouble(rawValue) is { } value)
        {
            return factoryName switch
            {
                "FromSeconds" => TimeSpan.FromSeconds(value),
                "FromMilliseconds" => TimeSpan.FromMilliseconds(value),
                "FromMinutes" => TimeSpan.FromMinutes(value),
                "FromHours" => TimeSpan.FromHours(value),
                "FromDays" => TimeSpan.FromDays(value),
                _ => (TimeSpan?)null,
            };
        }

        return null;
    }

    private static double? ToDouble(object value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        _ => null,
    };
}
