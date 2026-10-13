using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
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
//
// Extended to also check `ModPow`'s THIRD argument
// (`modulus`) for the literal value `0`: confirmed empirically that `BigInteger.ModPow(value, exponent,
// 0)` throws `DivideByZeroException` — a completely different failure mode from the negative-exponent
// check above (different argument position, different exception type). Confirmed a negative modulus
// does NOT throw (unlike the negative-exponent case) — only exactly zero does, so this check is
// deliberately narrow: `modulus == 0` alone, not any sign-based range check.
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

    // Separate DiagnosticDescriptor, same DiagnosticId, for the ModPow zero-modulus shape added in
    // проход 439 — kept apart from `Rule` above since it's a different exception type entirely
    // (DivideByZeroException, not ArgumentOutOfRangeException) at a different argument position,
    // same "same ID, distinct message shape" pattern InvalidCryptoAlgorithmKeySizeAnalyzer (RPAD0352)
    // already established for its own two call shapes.
    private static readonly DiagnosticDescriptor ModulusRule = new(
        DiagnosticId,
        title: "BigInteger.ModPow modulus is zero",
        messageFormat: "'{0}' always throws DivideByZeroException at runtime — modulus cannot be zero",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BigInteger.Pow(value, exponent) and BigInteger.ModPow(value, exponent, modulus) throw ArgumentOutOfRangeException when exponent is negative.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule, ModulusRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Pow" or "ModPow" } access)
            return;
        var ct = context.CancellationToken;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Numerics.BigInteger") return;

        if (invocation.ArgumentList.Arguments is [_, { Expression: var exponentArg }, ..]
            && context.SemanticModel.GetConstantValue(exponentArg, ct).Value is int exponent && exponent < 0
            && method.Parameters is [_, { Type: var exponentType }, ..]
            && (exponentType.SpecialType == SpecialType.System_Int32
                || exponentType.ToDisplayString() == "System.Numerics.BigInteger"))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
            return;
        }

        if (access.Name.Identifier.Text == "ModPow"
            && invocation.ArgumentList.Arguments is [_, _, { Expression: var modulusArg }, ..]
            && context.SemanticModel.GetConstantValue(modulusArg, ct).Value is int modulus && modulus == 0
            && method.Parameters is [_, _, { Type: var modulusType }, ..]
            && (modulusType.SpecialType == SpecialType.System_Int32
                || modulusType.ToDisplayString() == "System.Numerics.BigInteger"))
        {
            context.ReportDiagnostic(Diagnostic.Create(ModulusRule, invocation.GetLocation(), invocation.ToString()));
        }
    }
}
