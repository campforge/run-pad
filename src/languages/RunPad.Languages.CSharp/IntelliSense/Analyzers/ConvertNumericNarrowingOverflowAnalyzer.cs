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
        description: "Convert.ToByte/ToSByte/ToInt16/ToUInt16/ToInt32/ToUInt32/ToUInt64(int or long) throw OverflowException when the source value is outside the target type's range — unlike a plain unchecked cast, which would silently truncate.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, (long Min, long Max, string TypeKeyword)> TargetRangeByMethod =
        ImmutableDictionary.CreateRange(
            [
                KeyValuePair.Create("ToByte", (0L, (long)byte.MaxValue, "byte")),
                KeyValuePair.Create("ToSByte", ((long)sbyte.MinValue, (long)sbyte.MaxValue, "sbyte")),
                KeyValuePair.Create("ToInt16", ((long)short.MinValue, (long)short.MaxValue, "short")),
                KeyValuePair.Create("ToUInt16", (0L, (long)ushort.MaxValue, "ushort")),
                KeyValuePair.Create("ToInt32", ((long)int.MinValue, (long)int.MaxValue, "int")),
                KeyValuePair.Create("ToUInt32", (0L, (long)uint.MaxValue, "uint")),
                KeyValuePair.Create("ToUInt64", (0L, long.MaxValue, "ulong")),
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
        if (constant.Value is not (int or long)) return;
        var value = Convert.ToInt64(constant.Value);
        if (value >= range.Min && value <= range.Max) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 or SpecialType.System_Int64 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, value, range.TypeKeyword));
    }
}
