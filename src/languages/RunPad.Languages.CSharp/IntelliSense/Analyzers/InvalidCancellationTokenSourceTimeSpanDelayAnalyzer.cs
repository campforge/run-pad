using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести девяносто шестой проход, batch) — own pick, not a Roslynator port.
// `CancellationTokenSource(int millisecondsDelay)` is already covered by NegativeTimeoutArgumentAnalyzer
// (RPAD0233, проход 240), but that analyzer only recognizes an `int` literal via unary-minus pattern
// matching — it does not (and by its own mechanism cannot) see the sibling `CancellationTokenSource
// (TimeSpan delay)` overload. Confirmed empirically via scratch `dotnet run` script (not assumed) that the
// TimeSpan overload has a DIFFERENT boundary than the int one: `TimeSpan.Zero` is valid (unlike `0` isn't
// special-cased differently, both are fine), any OTHER negative value besides the special
// `Timeout.InfiniteTimeSpan` sentinel (-1ms) throws ArgumentOutOfRangeException, and — unlike the `int`
// overload, whose type already caps it near ~24.8 days — a `TimeSpan` can represent values large enough to
// also throw on the upper end. Reuses the same "extract a constant-foldable TimeSpan, call the real
// validator" technique as InvalidRegexMatchTimeoutAnalyzer (RPAD0279, проходы 294-295) — recognizing
// `TimeSpan.Zero`, `Timeout.InfiniteTimeSpan`, and `TimeSpan.FromXxx(literalNumber)`; other forms (`new
// TimeSpan(...)`, `FromTicks`, arithmetic, non-literal factory arguments) are not evaluated, same
// documented MVP scoping as that analyzer.
// Extended in the двести девяносто седьмой проход to also cover `Task.Delay(TimeSpan[, CancellationToken])`
// — RPAD0233 covers `Task.Delay(int)`/`Thread.Sleep(int)` too, and confirmed empirically (scratch
// `dotnet run` script) that `Task.Delay`'s `TimeSpan` overload has the SAME zero-ok/negative-throws/
// too-large-throws boundary as `CancellationTokenSource(TimeSpan)`. Deliberately does NOT probe
// `Task.Delay` by calling it with the user's ACTUAL delay value the way other "run the real validator"
// analyzers do — for a VALID delay that would start a real, unawaited background timer on every
// keystroke (analyzers re-run continuously while editing). Instead this probes safely by calling
// `Task.Delay(delay, alreadyCanceledToken)` — confirmed empirically that with an already-canceled token,
// Task.Delay still validates `delay` (throwing synchronously for an invalid one) but for a VALID delay
// returns an already-canceled `Task` immediately with no actual waiting and no dangling timer. `Thread.
// Sleep(TimeSpan)` has no such safe cancelable overload — calling it for a valid delay would BLOCK the
// analyzer thread for that real duration — so it is deliberately EXCLUDED here, not a "future extension"
// candidate, because the "call the real validator" technique cannot be applied to it safely at all.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCancellationTokenSourceTimeSpanDelayAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0280";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TimeSpan delay/timeout always throws",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — delay must be non-negative (or Timeout.InfiniteTimeSpan) and not too large",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CancellationTokenSource(TimeSpan delay) and Task.Delay(TimeSpan delay) throw ArgumentOutOfRangeException for any negative delay other than the special Timeout.InfiniteTimeSpan sentinel, and for a delay too large to represent as a millisecond count.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeTaskDelayInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList?.Arguments is not [{ Expression: var delayArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Threading.CancellationTokenSource") return;
        if (ctor.Parameters is not [{ Type.Name: "TimeSpan" }]) return;

        if (TryGetConstantTimeSpan(context.SemanticModel, delayArg, ct) is not { } delay) return;
        if (!AlwaysThrowsCancellationTokenSource(delay)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "new CancellationTokenSource(...)"));
    }

    private static void AnalyzeTaskDelayInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Delay" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var delayArg }, ..]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, Name: "Delay" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Tasks.Task") return;
        if (method.Parameters is not [{ Type.Name: "TimeSpan" }, ..]) return;

        if (TryGetConstantTimeSpan(context.SemanticModel, delayArg, ct) is not { } delay) return;
        if (!AlwaysThrowsTaskDelay(delay)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Task.Delay(...)"));
    }

    private static TimeSpan? TryGetConstantTimeSpan(SemanticModel model, ExpressionSyntax expr, CancellationToken ct)
    {
        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "Zero" } zeroAccess
            && model.GetSymbolInfo(zeroAccess, ct).Symbol is IFieldSymbol { ContainingType: { Name: "TimeSpan" } zeroType }
            && zeroType.ContainingNamespace?.ToDisplayString() == "System")
        {
            return TimeSpan.Zero;
        }

        if (expr is MemberAccessExpressionSyntax { Name.Identifier.Text: "InfiniteTimeSpan" } infiniteAccess
            && model.GetSymbolInfo(infiniteAccess, ct).Symbol is IFieldSymbol { ContainingType: { Name: "Timeout" } infiniteType }
            && infiniteType.ContainingNamespace?.ToDisplayString() == "System.Threading")
        {
            return Timeout.InfiniteTimeSpan;
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

    private static bool AlwaysThrowsCancellationTokenSource(TimeSpan delay)
    {
        try
        {
            using var cts = new CancellationTokenSource(delay);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static bool AlwaysThrowsTaskDelay(TimeSpan delay)
    {
        try
        {
            // An already-canceled token makes Task.Delay validate `delay` synchronously (still throwing
            // for an invalid one) but, for a VALID delay, return an already-canceled Task immediately —
            // no real waiting, no dangling background timer. Confirmed empirically before writing this;
            // calling Task.Delay(delay) directly with the user's real (valid) value would start a genuine
            // unawaited timer on every keystroke, since analyzers re-run continuously while editing.
            _ = Task.Delay(delay, new CancellationToken(canceled: true));
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
