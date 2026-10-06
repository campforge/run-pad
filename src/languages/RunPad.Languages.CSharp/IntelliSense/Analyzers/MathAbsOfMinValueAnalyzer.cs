using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести пятьдесят восьмой проход, batch) — own pick, not a Roslynator port. A correctness
// finding: two's-complement signed integers have one more negative value than positive value
// (`int.MinValue` is -2147483648, but `int.MaxValue` is only 2147483647), so there's no in-range
// positive representation of `Math.Abs(int.MinValue)` — `Math.Abs` is documented to throw
// `OverflowException` specifically for this case, for every signed integral overload (sbyte/short/int/
// long). The compiler's own constant-overflow check (CS0220) does NOT catch this — it only fires for
// constant EXPRESSIONS evaluated at compile time (like `-int.MinValue` written directly), not for an
// ordinary runtime method call like `Math.Abs(int.MinValue)`, which is exactly why this gap is real and
// worth flagging. Matched by the argument's own constant value against the resolved overload's
// parameter type's MinValue — catches both `Math.Abs(int.MinValue)` and the equivalent literal
// `Math.Abs(-2147483648)` the same way.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MathAbsOfMinValueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0251";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Math.Abs(MinValue) always throws OverflowException",
        messageFormat: "'Math.Abs({0})' always throws OverflowException at runtime — the minimum value of a signed integer type has no positive representation in the same type",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Math.Abs throws OverflowException when given the minimum value of a signed integral type, since that value's absolute value doesn't fit back in the same type.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<SpecialType, long> MinValueBySpecialType = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create(SpecialType.System_SByte, (long)sbyte.MinValue),
            KeyValuePair.Create(SpecialType.System_Int16, (long)short.MinValue),
            KeyValuePair.Create(SpecialType.System_Int32, (long)int.MinValue),
            KeyValuePair.Create(SpecialType.System_Int64, long.MinValue),
        ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Abs" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Abs" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Math") return;
        if (method.Parameters is not [{ Type: { } paramType }]) return;
        if (!MinValueBySpecialType.TryGetValue(paramType.SpecialType, out var minValue)) return;

        var constant = context.SemanticModel.GetConstantValue(arg, ct);
        if (!constant.HasValue || !TryToInt64(constant.Value, out var argValue)) return;
        if (argValue != minValue) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), argValue));
    }

    private static bool TryToInt64(object? value, out long result)
    {
        switch (value)
        {
            case sbyte v: result = v; return true;
            case short v: result = v; return true;
            case int v: result = v; return true;
            case long v: result = v; return true;
            default:
                result = 0;
                return false;
        }
    }
}
