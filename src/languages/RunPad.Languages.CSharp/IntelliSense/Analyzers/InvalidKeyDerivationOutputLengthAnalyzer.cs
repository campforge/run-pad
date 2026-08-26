using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (457-й проход, batch) — candidates #3/#4 from the nineteenth research subagent survey,
// re-verified independently before implementing. `Rfc2898DeriveBytes.Pbkdf2(..., int iterations,
// HashAlgorithmName, int outputLength)` (the modern non-obsolete static method — deliberately NOT the
// `Rfc2898DeriveBytes` constructors, which are `[Obsolete]`/SYSLIB0060) and `HKDF.Expand`/`HKDF.DeriveKey`
// (`outputLength` parameter). Bundled into one analyzer since both are sibling key-derivation APIs
// sharing the identical "non-positive output-related argument" shape, same "several call shapes, one
// diagnostic" precedent as `InvalidHashAlgorithmNameLiteralAnalyzer` (RPAD0426). Confirmed empirically:
// `Pbkdf2`'s `iterations` throws `ArgumentOutOfRangeException` for ≤ 0; its `outputLength` throws for
// negative. `HKDF.Expand`/`.DeriveKey`'s `outputLength` throws for ≤ 0 — confirmed hash-algorithm-
// INVARIANT (unlike `HKDF.Expand`'s SEPARATE, algorithm-dependent upper-bound check,
// `outputLength > 255 × hashLen`, which needs the literal hash name mapped to its digest length and is
// deliberately NOT checked here, matching the "provably invariant only" scoping discipline already
// established for `ImmutableArray.Create(items, start, length)`/RPAD0465). Confirmed via a correctly-
// sized 64-byte dummy PRK for the `HKDF.Expand` probe (a too-short PRK throws an unrelated
// `ArgumentException` about PRK length that must not be conflated with the `outputLength` check this
// analyzer targets — caught before shipping by re-testing with a properly-sized PRK). No
// `[SupportedOSPlatform]` gating, no side effects (pure computation). Confirmed invariant across
// en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidKeyDerivationOutputLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0469";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Key derivation argument always throws",
        messageFormat: "'{0}(...)' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Rfc2898DeriveBytes.Pbkdf2's iterations (must be positive) and outputLength (must be non-negative), and HKDF.Expand/DeriveKey's outputLength (must be positive), all throw ArgumentOutOfRangeException for a literal violating their own invariant bound.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Pbkdf2" or "Expand" or "DeriveKey" } access)
            return;
        var methodName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, ContainingType: { } containingType, Parameters: { } parameters } method)
            return;
        if (method.Name != methodName) return;
        var typeName = containingType.ToDisplayString();

        if (typeName == "System.Security.Cryptography.Rfc2898DeriveBytes" && methodName == "Pbkdf2")
        {
            AnalyzePbkdf2(context, invocation, parameters, ct);
        }
        else if (typeName == "System.Security.Cryptography.HKDF" && methodName is "Expand" or "DeriveKey")
        {
            AnalyzeHkdf(context, invocation, methodName, parameters, ct);
        }
    }

    private static void AnalyzePbkdf2(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, ImmutableArray<IParameterSymbol> parameters, System.Threading.CancellationToken ct)
    {
        var iterationsIndex = IndexOfParameter(parameters, "iterations");
        var outputLengthIndex = IndexOfParameter(parameters, "outputLength");
        var args = invocation.ArgumentList.Arguments;

        if (iterationsIndex >= 0 && iterationsIndex < args.Count
            && context.SemanticModel.GetConstantValue(args[iterationsIndex].Expression, ct).Value is int iterations && iterations <= 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Rfc2898DeriveBytes.Pbkdf2", $"iterations ({iterations}) must be a positive value"));
            return;
        }
        if (outputLengthIndex >= 0 && outputLengthIndex < args.Count
            && context.SemanticModel.GetConstantValue(args[outputLengthIndex].Expression, ct).Value is int outputLength && outputLength < 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Rfc2898DeriveBytes.Pbkdf2", $"outputLength ({outputLength}) cannot be negative"));
        }
    }

    private static void AnalyzeHkdf(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, string methodName, ImmutableArray<IParameterSymbol> parameters, System.Threading.CancellationToken ct)
    {
        var outputLengthIndex = IndexOfParameter(parameters, "outputLength");
        var args = invocation.ArgumentList.Arguments;
        if (outputLengthIndex < 0 || outputLengthIndex >= args.Count) return;
        if (context.SemanticModel.GetConstantValue(args[outputLengthIndex].Expression, ct).Value is not int outputLength || outputLength > 0) return;

        var prk = new byte[64];
        try
        {
            if (methodName == "Expand") HKDF.Expand(HashAlgorithmName.SHA256, prk, outputLength);
            else HKDF.DeriveKey(HashAlgorithmName.SHA256, prk, outputLength);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), $"HKDF.{methodName}", $"outputLength ({outputLength}) must be a positive value"));
        }
    }

    private static int IndexOfParameter(ImmutableArray<IParameterSymbol> parameters, string name)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].Name == name) return i;
        }
        return -1;
    }
}
