using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести тридцать третий проход, batch) — own pick, not a Roslynator port. A correctness
// finding, same "always true/false" theme as AlwaysFalseNonNegativeLengthComparisonAnalyzer/
// CompareNaNCorrectlyAnalyzer/BitwiseOperatorOnNonFlagsEnumAnalyzer, not a style simplification.
// `flags.HasFlag(x)` checks that every bit set in x is also set in flags — when x's underlying value is
// zero (typically the enum's `None` member), that condition is vacuously true for every possible flags
// value, so the call always returns true regardless of what flags actually is. Almost certainly not
// what the author intended (usually meant to ask "is flags exactly None", which needs `flags == 0`/
// `flags == MyEnum.None` instead). Matched via the argument's constant value, not just a literal
// `.None` spelling, so any zero-valued enum member (or `(MyEnum)0`) is caught the same way.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HasFlagWithZeroValueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0224";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "HasFlag with a zero-valued argument always returns true",
        messageFormat: "'flags.HasFlag(x)' always returns true when x's underlying value is zero — every value has all zero bits set; compare with '==' instead if you meant to test for the zero case specifically",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "HasFlag(x) checks that every bit of x is set in the receiver — for a zero-valued x, that condition holds for every possible receiver value, so the call is vacuously always true.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "HasFlag" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var argument }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "HasFlag" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Enum") return;

        var constant = context.SemanticModel.GetConstantValue(argument, ct);
        if (!constant.HasValue) return;
        var isZero = constant.Value switch
        {
            sbyte v => v == 0,
            byte v => v == 0,
            short v => v == 0,
            ushort v => v == 0,
            int v => v == 0,
            uint v => v == 0,
            long v => v == 0,
            ulong v => v == 0,
            _ => false,
        };
        if (!isZero) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
