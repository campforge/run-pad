using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (473-й проход, batch) — candidates #2/#3 from the thirty-sixth research subagent survey,
// re-verified independently before implementing. `X509Certificate.Export(X509ContentType[, string?])`
// (inherited unchanged by `X509Certificate2`) and `X509Certificate2Collection.Export(X509ContentType[,
// string?])` — bundled under one diagnostic (precedent RPAD0426), each with its OWN valid-value set,
// tested individually (overload-specific-validation trap). Both throw `CryptographicException`
// ("Invalid content type.") for an undefined `X509ContentType` value. Confirmed empirically on Linux:
// single-cert `Export` accepts only `Cert`(1)/`Pfx`(3) (plus `SerializedCert`(2), platform-gated, see
// below); collection `Export` additionally accepts `Pkcs7`(5) (a genuine overload difference — `Pkcs7`
// throws for the single-cert overload but succeeds for the collection overload, confirmed, not assumed).
// `Unknown`(0)/`Authenticode`(6)/any undefined int value throw `CryptographicException` on BOTH
// overloads. CROSS-PLATFORM REASONING (only tested on Linux, but grounded in how the BCL's shared/PAL
// split typically works, not just single-platform empiricism): only `SerializedCert` (single-cert) and
// `SerializedCert`+`SerializedStore` (collection) throw a DISTINCT, platform-specific
// `PlatformNotSupportedException` ("...are not supported on Unix") — every other invalid value throws
// the generic, platform-agnostic `CryptographicException("Invalid content type.")` with no
// platform-specific wording at all, strongly suggesting those values never reach a platform-specific
// code path on ANY OS (they fall through a shared switch's `default` case identically everywhere),
// unlike `SerializedCert`/`SerializedStore` which are explicitly routed to Windows-only CAPI
// serialization. This analyzer therefore probes via the REAL API and only flags on
// `CryptographicException` specifically — a `PlatformNotSupportedException` is caught separately and
// never flagged, so genuinely platform-gated values are excluded by observed behavior rather than a
// hardcoded exclusion list (if a future .NET version adds Windows support for more content types, this
// analyzer adapts automatically rather than needing an update). No `[SupportedOSPlatform]`-style gating
// beyond that per-value behavior; `X509ContentType` confirmed `int`-backed. No side effects beyond
// in-memory serialization of a throwaway self-signed cert. Culture-irrelevant (no formatting).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509ContentTypeExportAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0514";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509Certificate(Collection).Export content type is invalid",
        messageFormat: "'{0}.Export(...)' always throws CryptographicException at runtime — the content type is not supported for this export",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509Certificate.Export(X509ContentType) and X509Certificate2Collection.Export(X509ContentType) throw CryptographicException for a content type that isn't valid for that specific export shape.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Export" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Export",
                ContainingType: { } containingType,
                Parameters: [{ Type.Name: "X509ContentType" }, ..],
            })
        {
            return;
        }
        var typeName = containingType.ToDisplayString();
        var isCollection = typeName == "System.Security.Cryptography.X509Certificates.X509Certificate2Collection";
        if (!isCollection && typeName != "System.Security.Cryptography.X509Certificates.X509Certificate") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var contentTypeArg }, ..]) return;
        if (context.SemanticModel.GetConstantValue(contentTypeArg, ct).Value is not int contentTypeRaw) return;

        try
        {
            if (isCollection)
            {
                var collection = new X509Certificate2Collection();
                _ = collection.Export((X509ContentType)contentTypeRaw);
            }
            else
            {
                _ = DummyCertificate.Value.Export((X509ContentType)contentTypeRaw);
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            var shortName = isCollection ? "X509Certificate2Collection" : "X509Certificate";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), shortName));
        }
        catch (System.PlatformNotSupportedException)
        {
            // Genuinely platform-gated (e.g. SerializedCert/SerializedStore, Windows-only CAPI
            // serialization) — not our concern here, never flagged.
        }
    }

    private static class DummyCertificate
    {
        public static readonly X509Certificate2 Value = Create();

        private static X509Certificate2 Create()
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new CertificateRequest("CN=RunPadAnalyzerProbe", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            return request.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddDays(-1), System.DateTimeOffset.UtcNow.AddDays(365));
        }
    }
}
