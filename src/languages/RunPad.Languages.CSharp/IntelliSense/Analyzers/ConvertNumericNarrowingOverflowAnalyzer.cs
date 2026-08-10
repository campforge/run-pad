using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятый проход, batch) — own pick, not a Roslynator port. Distinct from
// IntegralParseOverflowAnalyzer (RPAD0258), which only handles a STRING argument being PARSED into a
// narrower integral type (`byte.Parse("300")`) — this covers the numeric-source overloads instead:
// `Convert.ToByte(int)`/`Convert.ToSByte(int)`/`Convert.ToInt16(int)`/`Convert.ToUInt16(int)` perform a
// CHECKED narrowing conversion that throws OverflowException for a source value outside the target type's
// range, unlike a plain unchecked `(byte)someInt` cast, which silently truncates instead — confirmed
// empirically via scratch `dotnet run` script, not assumed. Scoped to `int`/`long` source arguments;
// other source types (short, uint, etc.) are a future extension candidate, not attempted in this batch.
// Extended in the триста шестой проход to also cover a `long` source narrowing to `int`/`uint`/`ulong` —
// confirmed empirically that `Convert.ToInt32(long)` overflows for a too-large magnitude, and that
// `Convert.ToUInt32`/`ToUInt64` overflow for a NEGATIVE value regardless of whether the source is `int`
// or `long` (so those two now also accept an `int` source, not just `long` — `Convert.ToUInt32(-1)`
// throws just as much as `Convert.ToUInt32(-1L)`). `Convert.ToInt64` from a `long` source is identity and
// has no such overload, so it stays out of scope, same as `Convert.ToInt32` from an `int` source.
// Extended again in the триста сорок восьмой проход to also cover `double`/`float`/`decimal` SOURCE
// arguments (explicitly deferred back in проход 305), and one more TARGET method that only overflows
// meaningfully from these non-integral sources: `ToInt64` (a genuine overload exists for `double`/
// `float`/`decimal` sources, unlike the `int`/`long` sources this analyzer already handled, where
// `ToInt64` is identity/widening and therefore stayed out of scope). Deliberately does NOT add `ToChar`
// here — PROBE for this same batch caught it double-messaging with the already-shipped
// `ConvertToCharOverflowAnalyzer` (RPAD0285) for `int`/`long` sources; `ToChar`'s `double`/`float`/
// `decimal` coverage was added to THAT analyzer directly instead (same batch), since it's the more
// specific, dedicated home for this one target method. Range comparison now happens in `double` (safe
// for every source type here, including a `decimal` converted via a lossless-enough `(double)` cast for
// range-boundary purposes) while the ORIGINAL boxed constant value is still what gets reported in the
// diagnostic message, preserving each source type's own natural `ToString()` formatting rather than a
// converted one. Also caught and fixed a latent bug this extension would have introduced: `ToUInt64`'s
// upper bound had previously been hardcoded to `long.MaxValue` rather than the true `ulong.MaxValue` —
// harmless while only `int`/`long` sources were allowed (neither can ever exceed `long.MaxValue`
// anyway), but now that `double`/`decimal` sources are in scope, a value between `long.MaxValue` and
// `ulong.MaxValue` (e.g. `Convert.ToUInt64(1.5e19)`) is genuinely valid and must NOT be flagged —
// confirmed empirically before fixing, not just reasoned about.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConvertNumericNarrowingOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0286";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.ToXxx narrowing conversion always overflows",
        messageFormat: "'Convert.{0}({1})' always throws OverflowException at runtime — {1} is out of range for {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.ToByte/ToSByte/ToInt16/ToUInt16/ToInt32/ToUInt32/ToUInt64/ToInt64(int, long, double, float, or decimal) throw OverflowException when the source value is outside the target type's range — unlike a plain unchecked cast, which would silently truncate.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, (double Min, double Max, string TypeKeyword)> TargetRangeByMethod =
        ImmutableDictionary.CreateRange(
            [
                KeyValuePair.Create("ToByte", (0d, (double)byte.MaxValue, "byte")),
                KeyValuePair.Create("ToSByte", ((double)sbyte.MinValue, (double)sbyte.MaxValue, "sbyte")),
                KeyValuePair.Create("ToInt16", ((double)short.MinValue, (double)short.MaxValue, "short")),
                KeyValuePair.Create("ToUInt16", (0d, (double)ushort.MaxValue, "ushort")),
                KeyValuePair.Create("ToInt32", ((double)int.MinValue, (double)int.MaxValue, "int")),
                KeyValuePair.Create("ToUInt32", (0d, (double)uint.MaxValue, "uint")),
                KeyValuePair.Create("ToUInt64", (0d, (double)ulong.MaxValue, "ulong")),
                KeyValuePair.Create("ToInt64", ((double)long.MinValue, (double)long.MaxValue, "long")),
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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!TargetRangeByMethod.TryGetValue(methodName, out var range)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var valueArg }]) return;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(valueArg, ct);
        if (constant.Value is not (int or long or double or float or decimal)) return;
        var value = constant.Value switch
        {
            int i => (double)i,
            long l => (double)l,
            double d => d,
            float f => f,
            decimal m => (double)m,
            _ => 0d,
        };
        if (value >= range.Min && value <= range.Max) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;
        if (method.Parameters is not
            [
            {
                Type.SpecialType: SpecialType.System_Int32 or SpecialType.System_Int64
                        or SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal,
            },
            ])
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, constant.Value, range.TypeKeyword));
    }
}
