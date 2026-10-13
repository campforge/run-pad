using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `AesGcm(byte[] key, int tagSizeInBytes = 16)`,
// `AesCcm(byte[] key)`, `ChaCha20Poly1305(byte[] key)` — the AEAD cipher family, a previously-untouched
// vein within `System.Security.Cryptography` (this catalog already covers PEM import and
// HashAlgorithmName-consuming statics, but not these constructors). Confirmed empirically: a
// wrong-length `key` throws `CryptographicException` ("Specified key is not a valid size for this
// algorithm") — valid key lengths are 16/24/32 bytes for `AesGcm`/`AesCcm`, and EXACTLY 32 bytes (a
// single fixed size, no range) for `ChaCha20Poly1305`; `AesGcm`'s `tagSizeInBytes` throws `ArgumentException`
// outside 12-16 inclusive. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. IMPORTANT PLATFORM CAVEAT,
// confirmed via reflection and empirically on this machine: all three types carry
// `[UnsupportedOSPlatform]`/`[SupportedOSPlatform]` attributes — genuinely platform-gated. On an OS where
// `IsSupported` is `false`, even a VALID key throws `PlatformNotSupportedException` instead of succeeding,
// which would make this analyzer's own verdict wrong for a user running RunPad on such a platform. Guarded
// by checking each type's own `IsSupported` property at analysis time (reflecting the ANALYZER's actual
// runtime platform, the same one the user's compiled script would run on) — if unsupported, this analyzer
// stays silent rather than risk a wrong verdict either way. Scoped to a literal array whose length is known
// at the call site (`new byte[N]`/`new byte[] { ... }`), same "literal, not an arbitrary expression"
// scoping already established by `InvalidGuidByteArrayLengthAnalyzer` (RPAD0266).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidAeadCipherConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0463";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "AEAD cipher constructor argument always throws",
        messageFormat: "'new {0}(...)' always throws {1} at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "AesGcm/AesCcm/ChaCha20Poly1305 constructors throw CryptographicException for a wrong-length key, and AesGcm's tagSizeInBytes throws ArgumentException outside 12-16 bytes.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var keyArg }, ..] } argumentList) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } containingType })
            return;
        var typeName = containingType.ToDisplayString();
        if (typeName is not ("System.Security.Cryptography.AesGcm" or "System.Security.Cryptography.AesCcm" or "System.Security.Cryptography.ChaCha20Poly1305"))
            return;

        if (!IsSupported(typeName)) return;

        var keyLength = TryGetArrayLength(keyArg, context.SemanticModel, ct);
        int? tagSize = null;
        if (typeName == "System.Security.Cryptography.AesGcm" && argumentList.Arguments.Count >= 2)
        {
            if (context.SemanticModel.GetConstantValue(argumentList.Arguments[1].Expression, ct).Value is int t) tagSize = t;
        }
        if (keyLength is null && tagSize is null) return;

        var key = new byte[keyLength ?? 16];
        var (threw, exceptionType, reason) = typeName switch
        {
            "System.Security.Cryptography.AesGcm" => ProbeAesGcm(key, tagSize ?? 16, keyLength is not null, tagSize is not null),
            "System.Security.Cryptography.AesCcm" => ProbeAesCcm(key),
            _ => ProbeChaCha20Poly1305(key),
        };
        if (!threw) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), containingType.Name, exceptionType, reason));
    }

    private static bool IsSupported(string typeName) => typeName switch
    {
        "System.Security.Cryptography.AesGcm" => AesGcm.IsSupported,
        "System.Security.Cryptography.AesCcm" => AesCcm.IsSupported,
        _ => ChaCha20Poly1305.IsSupported,
    };

    private static (bool Threw, string ExceptionType, string Reason) ProbeAesGcm(byte[] key, int tagSize, bool checkKey, bool checkTag)
    {
        try
        {
            using var a = new AesGcm(key, tagSize);
            return (false, "", "");
        }
        catch (CryptographicException) when (checkKey)
        {
            return (true, "CryptographicException", $"the key length ({key.Length} bytes) is not valid — AesGcm requires 16, 24, or 32 bytes");
        }
        catch (System.ArgumentException) when (checkTag)
        {
            return (true, "ArgumentException", $"the tag size ({tagSize} bytes) is not valid — AesGcm requires 12-16 bytes");
        }
        catch
        {
            return (false, "", "");
        }
    }

    private static (bool Threw, string ExceptionType, string Reason) ProbeAesCcm(byte[] key)
    {
        try
        {
            using var a = new AesCcm(key);
            return (false, "", "");
        }
        catch (CryptographicException)
        {
            return (true, "CryptographicException", $"the key length ({key.Length} bytes) is not valid — AesCcm requires 16, 24, or 32 bytes");
        }
    }

    private static (bool Threw, string ExceptionType, string Reason) ProbeChaCha20Poly1305(byte[] key)
    {
        try
        {
            using var a = new ChaCha20Poly1305(key);
            return (false, "", "");
        }
        catch (CryptographicException)
        {
            return (true, "CryptographicException", $"the key length ({key.Length} bytes) is not valid — ChaCha20Poly1305 requires exactly 32 bytes");
        }
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
        return null;
    }
}
