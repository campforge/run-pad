using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста третий проход, batch) — own pick, not a Roslynator port. Same "run the real
// validator" theme as the integral-overflow family (IntegralParseOverflowAnalyzer, RPAD0258), applied to
// `Convert.ToChar(int)`/`Convert.ToChar(long)` instead of string parsing: throws `OverflowException` when
// the value is outside `char`'s valid range (0-65535) — confirmed empirically via scratch `dotnet run`
// script, not assumed. Scoped to the `int`/`long` overloads with a constant-foldable argument (via
// `GetConstantValue`, so `int.MaxValue`-style constants resolve too, not just numeric-literal tokens);
// `Convert.ToChar` also has `float`/`double`/`decimal`/`string`/`object`/`bool`/`DateTime` overloads not
// handled here — a future extension candidate, not attempted in this batch.
// Extended in the триста четвёртый проход to also cover `SByte`/`UInt32`/`UInt64` (confirmed empirically
// that all three can overflow char's range, unlike `Byte`/`UInt16`, whose own value ranges (0-255,
// 0-65535) can never exceed char's, so those two are deliberately left out — there is nothing for this
// analyzer to ever catch there).
// Extended again in the триста сорок восьмой проход to also cover `float`/`double`/`decimal` — the
// case explicitly deferred back in проход 303. Confirmed empirically to throw identically for all
// three. Kept as an extension of THIS analyzer rather than added to
// ConvertNumericNarrowingOverflowAnalyzer (RPAD0286, which gained the same three source types in this
// same проход for its own different target methods) — PROBE caught that RPAD0286 adding `ToChar` would
// double-message with this analyzer's existing `int`/`long` coverage for the exact same defect, so
// `ToChar`'s non-integral-source coverage belongs here instead, its more specific, dedicated home.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConvertToCharOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0285";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.ToChar argument overflows char range",
        messageFormat: "'Convert.ToChar({0})' always throws OverflowException at runtime — value must be between 0 and 65535",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.ToChar(int/long/sbyte/uint/ulong/double/float/decimal) throws OverflowException when the value is outside char's valid range (0-65535).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToChar" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var valueArg }]) return;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(valueArg, ct);
        if (constant.Value is not (int or long or sbyte or uint or ulong or double or float or decimal)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToChar" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;
        if (method.Parameters is not
            [
            {
                Type.SpecialType: SpecialType.System_Int32 or SpecialType.System_Int64
                        or SpecialType.System_SByte or SpecialType.System_UInt32 or SpecialType.System_UInt64
                        or SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal,
            },
            ])
        {
            return;
        }

        if (!IsOutOfCharRange(constant.Value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), constant.Value));
    }

    private static bool IsOutOfCharRange(object value) => value switch
    {
        int i => i is < char.MinValue or > char.MaxValue,
        long l => l is < char.MinValue or > char.MaxValue,
        sbyte sb => sb < 0, // sbyte's max (127) always fits within char's range, only the negative side can overflow
        uint u => u > char.MaxValue,
        ulong ul => ul > char.MaxValue,
        double d => d is < char.MinValue or > char.MaxValue,
        float f => f is < char.MinValue or > char.MaxValue,
        decimal m => m is < char.MinValue or > char.MaxValue,
        _ => false,
    };
}
