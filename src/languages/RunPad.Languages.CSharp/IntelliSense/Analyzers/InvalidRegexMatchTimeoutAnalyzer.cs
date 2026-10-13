using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. New theme,
// still "run the real validator", but on a `TimeSpan` argument rather than a numeric/flags one:
// `new Regex(pattern, options, matchTimeout)` throws ArgumentOutOfRangeException when `matchTimeout` is
// zero, negative (except the special sentinel `Regex.InfiniteMatchTimeout`, itself a negative TimeSpan),
// or exceeds ~24.8 days (confirmed empirically via scratch `dotnet run` script, not assumed — the exact
// upper boundary is deliberately NOT hardcoded here; the analyzer calls the REAL constructor so the BCL's
// own boundary logic decides). Scoped to constant-foldable `matchTimeout` expressions this analyzer can
// itself evaluate into an actual `TimeSpan` value — `TimeSpan.Zero` and `TimeSpan.FromXxx(literalNumber)`
// (Seconds/Milliseconds/Minutes/Hours/Days) — general expressions (a `new TimeSpan(...)` constructor call,
// arithmetic, `TimeSpan.FromTicks`, non-literal factory arguments) are NOT evaluated and are silently
// skipped, same "MVP first, extend later" scoping as other analyzers in this catalog. Only the `Regex`
// CONSTRUCTOR overload is covered here — the static `Regex.IsMatch`/`Match`/`Matches`/`Replace`/`Split`
// overloads that also accept a `matchTimeout` are candidates for a follow-up extension.
// Extended to also cover those static methods — confirmed
// empirically that all of them (`IsMatch`/`Match`/`Matches`/`Replace` (both the string and
// `MatchEvaluator` overloads)/`Split`) throw identically to the constructor for the same `matchTimeout`
// value, so the same proxy validator (constructing a throwaway `Regex` instance) is reused rather than
// calling each individual static method with its own placeholder arguments.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRegexMatchTimeoutAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0279";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Regex matchTimeout always throws",
        messageFormat: "'{0}(...)' always throws ArgumentOutOfRangeException at runtime — matchTimeout must be positive (or Regex.InfiniteMatchTimeout) and no more than ~24.8 days",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Regex's matchTimeout parameter must be a positive TimeSpan not exceeding Int32.MaxValue milliseconds, or the special Regex.InfiniteMatchTimeout sentinel — any other zero, negative, or too-large value throws ArgumentOutOfRangeException.");

    private static readonly ImmutableHashSet<string> StaticMethodNames = ImmutableHashSet.Create(
        "IsMatch", "Match", "Matches", "Replace", "Split");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList?.Arguments is not [_, _, { Expression: var timeoutArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (!IsRegexType(ctor.ContainingType)) return;
        if (ctor.Parameters is not [_, _, { Type.Name: "TimeSpan" }]) return;

        if (TryGetConstantTimeSpan(context.SemanticModel, timeoutArg, ct) is not { } timeout) return;
        if (!AlwaysThrows(timeout)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "new Regex"));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!StaticMethodNames.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [.., { Expression: var timeoutArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (!IsRegexType(method.ContainingType)) return;
        if (method.Parameters is not [.., { Type.Name: "TimeSpan" }]) return;

        if (TryGetConstantTimeSpan(context.SemanticModel, timeoutArg, ct) is not { } timeout) return;
        if (!AlwaysThrows(timeout)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), $"Regex.{methodName}"));
    }

    private static bool IsRegexType(INamedTypeSymbol? type) =>
        type is { Name: "Regex" } && type.ContainingNamespace?.ToDisplayString() == "System.Text.RegularExpressions";

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

    private static bool AlwaysThrows(TimeSpan matchTimeout)
    {
        try
        {
            _ = new Regex("a", RegexOptions.None, matchTimeout);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
