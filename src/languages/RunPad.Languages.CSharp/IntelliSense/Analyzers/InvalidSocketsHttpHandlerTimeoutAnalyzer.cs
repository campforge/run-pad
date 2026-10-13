using System.Collections.Immutable;
using System.Net.Http;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #4 from the thirteenth research subagent survey,
// re-verified independently before implementing. `SocketsHttpHandler`'s seven `TimeSpan`-typed
// properties — `ConnectTimeout`, `PooledConnectionLifetime`, `PooledConnectionIdleTimeout`,
// `Expect100ContinueTimeout`, `KeepAlivePingTimeout`, `KeepAlivePingDelay`, `ResponseDrainTimeout` — each
// validated individually by its own real setter, with genuinely DIFFERENT per-property boundaries
// confirmed empirically rather than assumed: most accept `TimeSpan.Zero` and the `-1ms` infinite
// sentinel, reject any other negative value; `ConnectTimeout` additionally rejects `TimeSpan.Zero` itself
// (unlike its siblings); `KeepAlivePingTimeout`/`KeepAlivePingDelay` require either ≥ 1 second or the
// `-1ms` infinite sentinel — a 500ms value throws even though it's positive. Because the boundaries
// genuinely differ per property, this analyzer dispatches to the REAL property setter for whichever
// property matched (never hand-derives a shared boundary), same "run the real validator" discipline as
// the rest of this catalog. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects. Reuses
// the same "extract a constant-foldable TimeSpan" technique already established by
// `InvalidHttpClientTimeoutAnalyzer` (RPAD0446) and `InvalidCancellationTokenSourceTimeSpanDelayAnalyzer`
// (RPAD0280).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSocketsHttpHandlerTimeoutAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0448";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SocketsHttpHandler TimeSpan property assignment always throws",
        messageFormat: "'SocketsHttpHandler.{0} = ...' always throws ArgumentOutOfRangeException at runtime — value is outside the valid range for {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SocketsHttpHandler.ConnectTimeout/PooledConnectionLifetime/PooledConnectionIdleTimeout/Expect100ContinueTimeout/KeepAlivePingTimeout/KeepAlivePingDelay/ResponseDrainTimeout each throw ArgumentOutOfRangeException for a value outside their own individually-validated range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> PropertyNames = ImmutableHashSet.Create(
        "ConnectTimeout", "PooledConnectionLifetime", "PooledConnectionIdleTimeout",
        "Expect100ContinueTimeout", "KeepAlivePingTimeout", "KeepAlivePingDelay", "ResponseDrainTimeout");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName } access)
            return;
        if (!PropertyNames.Contains(propertyName)) return;

        var ct = context.CancellationToken;
        if (TryGetConstantTimeSpan(context.SemanticModel, assignment.Right, ct) is not { } value) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Net.Http.SocketsHttpHandler") return;

        if (!AlwaysThrows(propertyName, value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName));
    }

    private static bool AlwaysThrows(string propertyName, System.TimeSpan value)
    {
        using var handler = new SocketsHttpHandler();
        try
        {
            switch (propertyName)
            {
                case "ConnectTimeout": handler.ConnectTimeout = value; break;
                case "PooledConnectionLifetime": handler.PooledConnectionLifetime = value; break;
                case "PooledConnectionIdleTimeout": handler.PooledConnectionIdleTimeout = value; break;
                case "Expect100ContinueTimeout": handler.Expect100ContinueTimeout = value; break;
                case "KeepAlivePingTimeout": handler.KeepAlivePingTimeout = value; break;
                case "KeepAlivePingDelay": handler.KeepAlivePingDelay = value; break;
                case "ResponseDrainTimeout": handler.ResponseDrainTimeout = value; break;
            }
            return false;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return true;
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
