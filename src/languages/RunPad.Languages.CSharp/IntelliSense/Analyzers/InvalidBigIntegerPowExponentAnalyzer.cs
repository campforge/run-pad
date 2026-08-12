using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят первый проход, batch) — own pick, not a Roslynator port. Same
// "literal argument that always throws" theme as NegativeCountArgumentAnalyzer (RPAD0232):
// `BigInteger.Pow(BigInteger value, int exponent)` and `BigInteger.ModPow(BigInteger value, BigInteger
// exponent, BigInteger modulus)` both throw ArgumentOutOfRangeException for a negative `exponent` —
// confirmed empirically via scratch `dotnet run` script (both share the identical formula and
// exception type, and `exponent` sits at argument position 1 in both signatures, so one analyzer
// covers both method names). Caught via a failing test (not PROBE, since both single-item PROBEs used
// literal `-1` for `exponent` and happened to pass regardless): `Pow`'s `exponent` parameter is
// genuinely `int`, but `ModPow`'s `exponent` parameter is `BigInteger` — confirmed via reflection on
// `typeof(BigInteger).GetMethods()`. A literal `int` argument implicitly converted to `BigInteger`
// still constant-folds to a plain boxed `int` via `GetConstantValue` (the literal's own syntactic type,
// not the target parameter's type), so the value-extraction half was always fine; only the parameter-
// type gate needed relaxing to accept EITHER `Int32` (for `Pow`) or `System.Numerics.BigInteger` (for
// `ModPow`) at position 1, rather than requiring `Int32` unconditionally.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBigIntegerPowExponentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0350";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BigInteger.Pow/ModPow exponent is negative",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — exponent cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BigInteger.Pow(value, exponent) and BigInteger.ModPow(value, exponent, modulus) throw ArgumentOutOfRangeException when exponent is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Pow" or "ModPow" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var exponentArg }, ..]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(exponentArg, ct).Value is not int exponent || exponent >= 0)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Numerics.BigInteger") return;
        if (method.Parameters is not [_, { Type: var exponentType }, ..]) return;
        if (exponentType.SpecialType != SpecialType.System_Int32
            && exponentType.ToDisplayString() != "System.Numerics.BigInteger")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
    }
}
