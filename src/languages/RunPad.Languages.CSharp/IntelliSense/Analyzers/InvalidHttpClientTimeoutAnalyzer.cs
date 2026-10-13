using System.Collections.Immutable;
using System.Net.Http;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the thirteenth research subagent survey,
// re-verified independently before implementing. `HttpClient.Timeout` (TimeSpan) — first analyzer in this
// catalog touching `System.Net.Http`, an entirely new configuration-object family the agent found after
// being pointed at the productive "range-validated property setter" vein (NumberFormatInfo/
// DateTimeFormatInfo/JsonSerializerOptions/XmlWriterSettings/XmlReaderSettings, проходы 448-450).
// Confirmed empirically, single exception type: `ArgumentOutOfRangeException` for `TimeSpan.Zero` (unlike
// `CancellationTokenSource(TimeSpan)`/`Task.Delay(TimeSpan)`, RPAD0280, where zero IS valid — this
// property requires STRICTLY greater than zero), any other negative value, or a value exceeding
// `Int32.MaxValue` milliseconds (~24.8 days); `Timeout.InfiniteTimeSpan` remains the valid "no timeout"
// sentinel. Confirmed the range check fires even on an `HttpClient` that has already sent a request
// (normally throws `InvalidOperationException` for further mutation) — the invalid-value check runs
// FIRST, so a literal invalid value always throws `ArgumentOutOfRangeException` regardless of the
// receiver's prior use, sidestepping the schema/state-dependence trap this catalog has hit before.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP, and confirmed no side effects (construction and
// assignment never touch the network, even with no listener present). Reuses the same "extract a
// constant-foldable TimeSpan, call the real validator" technique already established by
// `InvalidCancellationTokenSourceTimeSpanDelayAnalyzer` (RPAD0280) and `InvalidTimeZoneCustomOffsetAnalyzer`
// (RPAD0438) — recognizing `TimeSpan.Zero`, `Timeout.InfiniteTimeSpan`, and `TimeSpan.FromXxx(literal)`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHttpClientTimeoutAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0446";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "HttpClient.Timeout assignment always throws",
        messageFormat: "'HttpClient.Timeout = ...' always throws ArgumentOutOfRangeException at runtime — Timeout must be greater than zero (or Timeout.InfiniteTimeSpan) and not too large",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "HttpClient.Timeout throws ArgumentOutOfRangeException for TimeSpan.Zero, any other negative value other than Timeout.InfiniteTimeSpan, or a value too large to represent as a millisecond count.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Timeout" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "Timeout" })
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Net.Http.HttpClient") return;

        if (TryGetConstantTimeSpan(context.SemanticModel, assignment.Right, ct) is not { } value) return;

        try
        {
            using var client = new HttpClient();
            client.Timeout = value;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation()));
        }
    }

    private static System.TimeSpan? TryGetConstantTimeSpan(SemanticModel model, ExpressionSyntax expr, CancellationToken ct)
    {
        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "Zero" } zeroAccess
            && model.GetSymbolInfo(zeroAccess, ct).Symbol is IFieldSymbol { ContainingType: { Name: "TimeSpan" } zeroType }
            && zeroType.ContainingNamespace?.ToDisplayString() == "System")
        {
            return System.TimeSpan.Zero;
        }

        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "InfiniteTimeSpan" } infiniteAccess
            && model.GetSymbolInfo(infiniteAccess, ct).Symbol is IFieldSymbol { ContainingType: { Name: "Timeout" } infiniteType }
            && infiniteType.ContainingNamespace?.ToDisplayString() == "System.Threading")
        {
            return System.Threading.Timeout.InfiniteTimeSpan;
        }

        if (expr is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var factoryName }, ArgumentList.Arguments: [{ Expression: var factoryArg }] } factoryInvocation
            && model.GetSymbolInfo(factoryInvocation, ct).Symbol is IMethodSymbol { ContainingType: { Name: "TimeSpan" } factoryType }
            && factoryType.ContainingNamespace?.ToDisplayString() == "System"
            && model.GetConstantValue(factoryArg, ct) is { HasValue: true, Value: { } rawValue }
            && ToDouble(rawValue) is { } value)
        {
            return factoryName switch
            {
                "FromSeconds" => System.TimeSpan.FromSeconds(value),
                "FromMilliseconds" => System.TimeSpan.FromMilliseconds(value),
                "FromMinutes" => System.TimeSpan.FromMinutes(value),
                "FromHours" => System.TimeSpan.FromHours(value),
                "FromDays" => System.TimeSpan.FromDays(value),
                _ => (System.TimeSpan?)null,
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
