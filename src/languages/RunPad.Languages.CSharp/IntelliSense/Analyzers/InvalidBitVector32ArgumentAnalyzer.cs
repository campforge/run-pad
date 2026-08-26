using System.Collections.Immutable;
using System.Collections.Specialized;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (467-й проход, batch) — candidate #1 from the thirtieth research subagent survey,
// re-verified independently before implementing. `System.Collections.Specialized.BitVector32` static
// helpers, bundled under one diagnostic (precedent RPAD0426): `CreateMask(int previous)` — throws
// `InvalidOperationException` ("Bit vector is full.") for exactly ONE sentinel value,
// `previous == int.MinValue` (0x80000000) — confirmed a true sentinel, not a range (every other tested
// value, including `-1`/`-2`/`int.MaxValue`, does NOT throw); `CreateSection(short maxValue)` (the
// 1-arg overload only — the 2-arg `(short, Section)` overload needs a non-literal `Section`, out of
// scope) — throws `ArgumentOutOfRangeException` for `maxValue <= 0`. Both static (confirmed via
// reflection), no `[SupportedOSPlatform]` gating, no side effects — pure struct/bit arithmetic.
// Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBitVector32ArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0502";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BitVector32 argument always throws",
        messageFormat: "'BitVector32.{0}({1})' always throws {2} at runtime — {3}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BitVector32.CreateMask(int.MinValue) throws InvalidOperationException, and CreateSection(short) throws ArgumentOutOfRangeException for a non-positive maxValue.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        var isCreateMask = methodName == "CreateMask";
        if (!isCreateMask && methodName != "CreateSection") return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                ContainingType: { } containingType,
            } method)
        {
            return;
        }
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Collections.Specialized.BitVector32") return;

        if (isCreateMask)
        {
            if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }]) return;
            if (invocation.ArgumentList.Arguments is not [{ Expression: var previousArg }]) return;
            if (context.SemanticModel.GetConstantValue(previousArg, ct).Value is not int previous) return;
            if (previous != int.MinValue) return;

            try
            {
                _ = BitVector32.CreateMask(previous);
            }
            catch (System.InvalidOperationException)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, previous, "InvalidOperationException", "the bit vector is full (previous is int.MinValue)"));
            }
        }
        else
        {
            if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int16 }]) return;
            if (invocation.ArgumentList.Arguments is not [{ Expression: var maxValueArg }]) return;
            if (ToInt16Constant(context.SemanticModel.GetConstantValue(maxValueArg, ct)) is not { } maxValue) return;
            if (maxValue > 0) return;

            try
            {
                _ = BitVector32.CreateSection(maxValue);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, maxValue, "ArgumentOutOfRangeException", "maxValue must be positive"));
            }
        }
    }

    private static short? ToInt16Constant(Optional<object?> constant) => constant.Value switch
    {
        short s => s,
        int i and >= short.MinValue and <= short.MaxValue => (short)i,
        _ => null,
    };
}
