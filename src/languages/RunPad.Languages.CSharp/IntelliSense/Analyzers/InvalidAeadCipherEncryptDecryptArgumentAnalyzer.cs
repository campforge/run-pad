using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the fortieth research subagent survey,
// re-verified independently before implementing (the agent's own transcript understated a trap — see
// below). `AesGcm`/`AesCcm`/`ChaCha20Poly1305`'s instance `Encrypt`/`Decrypt` methods — a genuinely
// different call site from the already-covered `InvalidAeadCipherConstructorArgumentAnalyzer`
// (RPAD0463), which only checks the CONSTRUCTOR's `key`/`tagSizeInBytes` arguments. Here, a literal
// `nonce`/`tag` byte array/`stackalloc` buffer of the wrong length always throws `ArgumentException`
// ("The specified nonce/tag is not a valid size for this algorithm" or "...does not match the expected
// size of N"), independent of the plaintext/ciphertext/key/associatedData.
// SELF-CAUGHT CORRECTION (before implementing): the research agent's `AesGcm` tag-length transcript used
// the type's DEFAULT tag size (16, from omitting the constructor's optional `tagSizeInBytes`) and
// concluded any tag literal outside the *overall* 12-16 range always throws — re-verified independently
// and found this is FALSE when `tagSizeInBytes` is explicitly specified at construction: `new
// AesGcm(key, 12)` then rejects EVERY `tag` length except exactly 12, including 16 (which IS in the
// general 12-16 range but does not match this particular instance's configured size) — confirmed
// empirically both ways (`tagSizeInBytes: 12` rejects tag=16; the deprecated single-`byte[]`-argument
// constructor, which defaults to 16, accepts any of 12-16 freely). This means AesGcm's `tag` length
// validity is INSTANCE-CONSTRUCTION-DEPENDENT, not a pure function of the literal alone — violates this
// catalog's "always throws independent of any other state" bar (ambient-state trap) — so AesGcm's `tag`
// argument is DELIBERATELY EXCLUDED here; only its `nonce` (fixed exactly 12 bytes, confirmed via
// `AesGcm.NonceByteSizes.{MinSize,MaxSize}` both being 12, independent of construction) is checked.
// `AesCcm`'s constructor takes ONLY a key (no size-fixing parameter), and empirically its `tag` length is
// freely re-chosen per Encrypt/Decrypt call (confirmed: the same `AesCcm` instance accepts every one of
// 4/6/8/10/12/14/16-byte tags across repeated calls) — so both `AesCcm.NonceByteSizes`/`TagByteSizes`
// (the latter requiring an EVEN length, `SkipSize` = 2) are safe, construction-independent checks.
// `ChaCha20Poly1305` fixes both `nonce` (12) and `tag` (16) as hardcoded constants with no
// `NonceByteSizes`/`TagByteSizes` property exposed (confirmed via reflection — property absent) and no
// size-related constructor parameter, so both are safe fixed-value checks. IMPORTANT PLATFORM CAVEAT
// (same as RPAD0463): all three types are genuinely platform-gated — guarded by each type's own
// `IsSupported` at analysis time. Scoped to a literal array/`stackalloc` buffer whose length is known at
// the call site, same "literal, not an arbitrary expression" scoping as RPAD0463/`InvalidGuidByteArrayLengthAnalyzer`
// (RPAD0266).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidAeadCipherEncryptDecryptArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0520";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "AEAD cipher Encrypt/Decrypt nonce or tag argument always throws",
        messageFormat: "'{0}.{1}(...)' always throws ArgumentException at runtime — the {2} length ({3} bytes) is not valid for {0} (must be {4})",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "AesGcm/AesCcm/ChaCha20Poly1305's Encrypt/Decrypt throw ArgumentException for a nonce or tag argument of the wrong length: AesGcm nonce must be exactly 12 bytes (its tag length depends on how the instance was constructed, so it is not checked here); AesCcm nonce must be 7-13 bytes and tag 4-16 bytes in steps of 2; ChaCha20Poly1305 nonce must be exactly 12 bytes and tag exactly 16.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Encrypt" or "Decrypt" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        var typeName = containingType.ToDisplayString();
        if (typeName is not ("System.Security.Cryptography.AesGcm" or "System.Security.Cryptography.AesCcm" or "System.Security.Cryptography.ChaCha20Poly1305"))
            return;
        if (!IsSupported(typeName)) return;

        foreach (var paramName in ParamsToCheck(typeName))
        {
            var argExpr = GetArgumentFor(invocation, method, paramName);
            if (argExpr is null) continue;
            if (TryGetArrayLength(argExpr, context.SemanticModel, ct) is not { } length) continue;

            var sizes = GetValidSizes(typeName, paramName);
            if (sizes is not { } s || IsValidLength(length, s)) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Rule, argExpr.GetLocation(), containingType.Name, access.Name.Identifier.Text, paramName, length, DescribeValidLengths(s)));
        }
    }

    private static IEnumerable<string> ParamsToCheck(string typeName) => typeName switch
    {
        "System.Security.Cryptography.AesGcm" => ["nonce"],
        _ => ["nonce", "tag"],
    };

    private static bool IsSupported(string typeName) => typeName switch
    {
        "System.Security.Cryptography.AesGcm" => AesGcm.IsSupported,
        "System.Security.Cryptography.AesCcm" => AesCcm.IsSupported,
        _ => ChaCha20Poly1305.IsSupported,
    };

    private static (int Min, int Max, int Skip)? GetValidSizes(string typeName, string paramName) => (typeName, paramName) switch
    {
        ("System.Security.Cryptography.AesGcm", "nonce") => (AesGcm.NonceByteSizes.MinSize, AesGcm.NonceByteSizes.MaxSize, AesGcm.NonceByteSizes.SkipSize),
        ("System.Security.Cryptography.AesCcm", "nonce") => (AesCcm.NonceByteSizes.MinSize, AesCcm.NonceByteSizes.MaxSize, AesCcm.NonceByteSizes.SkipSize),
        ("System.Security.Cryptography.AesCcm", "tag") => (AesCcm.TagByteSizes.MinSize, AesCcm.TagByteSizes.MaxSize, AesCcm.TagByteSizes.SkipSize),
        ("System.Security.Cryptography.ChaCha20Poly1305", "nonce") => (12, 12, 1),
        ("System.Security.Cryptography.ChaCha20Poly1305", "tag") => (16, 16, 1),
        _ => null,
    };

    private static bool IsValidLength(int length, (int Min, int Max, int Skip) sizes)
    {
        if (length < sizes.Min || length > sizes.Max) return false;
        return sizes.Skip <= 1 || (length - sizes.Min) % sizes.Skip == 0;
    }

    private static string DescribeValidLengths((int Min, int Max, int Skip) sizes)
    {
        if (sizes.Min == sizes.Max) return $"exactly {sizes.Min} bytes";
        if (sizes.Skip <= 1) return $"{sizes.Min}-{sizes.Max} bytes";
        return $"{sizes.Min}-{sizes.Max} bytes in increments of {sizes.Skip}";
    }

    private static ExpressionSyntax? GetArgumentFor(InvocationExpressionSyntax invocation, IMethodSymbol method, string paramName)
    {
        var parameters = method.Parameters;
        var arguments = invocation.ArgumentList.Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.NameColon is { } nameColon)
            {
                if (nameColon.Name.Identifier.Text == paramName) return argument.Expression;
                continue;
            }
            if (i < parameters.Length && parameters[i].Name == paramName) return argument.Expression;
        }
        return null;
    }

    private static int? TryGetArrayLength(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        if (expression is ArrayCreationExpressionSyntax arrayCreation)
        {
            if (arrayCreation.Initializer is { } init) return init.Expressions.Count;
            if (arrayCreation.Type.RankSpecifiers is [{ Sizes: [var sizeExpr] }, ..]
                && semanticModel.GetConstantValue(sizeExpr, ct).Value is int size)
            {
                return size;
            }
            return null;
        }
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return implicitInit.Expressions.Count;
        if (expression is CollectionExpressionSyntax collectionExpr)
            return collectionExpr.Elements.Count;
        if (expression is StackAllocArrayCreationExpressionSyntax stackAlloc)
        {
            if (stackAlloc.Initializer is { } stackInit) return stackInit.Expressions.Count;
            if (stackAlloc.Type is ArrayTypeSyntax { RankSpecifiers: [{ Sizes: [var sizeExpr] }, ..] }
                && semanticModel.GetConstantValue(sizeExpr, ct).Value is int size)
            {
                return size;
            }
            return null;
        }
        if (expression is ImplicitStackAllocArrayCreationExpressionSyntax { Initializer: { } implicitStackInit })
            return implicitStackInit.Expressions.Count;
        return null;
    }
}
