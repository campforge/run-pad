using System;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (четыреста первый проход, batch) — own pick, not a Roslynator port; fifth candidate from the
// second research subagent survey (проход 397, read-only tools, doc-based hunches only), re-verified
// empirically before implementing. `X509Certificate2.CreateFromPem(string)` — sibling of RPAD0392's
// `RSA.ImportFromPem`, same `ReadOnlySpan<char>`-not-`string` parameter shape (confirmed via reflection
// before writing any matching code this time, having just learned that lesson on RPAD0392). Static method
// with two overloads — this analyzer scopes to the 1-arg `(ReadOnlySpan<char> certPem)` overload only,
// deliberately not the 2-arg `(ReadOnlySpan<char> certPem, ReadOnlySpan<char> keyPem)` cert+private-key
// overload (same single-shape restraint as this catalog's other analyzers). Confirmed empirically this is
// a single-exception-type situation: empty string, garbage text, and a wrong-label PEM block (e.g.
// "PUBLIC KEY" instead of "CERTIFICATE") all throw `System.Security.Cryptography.CryptographicException`.
// A REAL freshly-generated self-signed certificate PEM (via a throwaway `CertificateRequest`/
// `CreateSelfSigned`/`ExportCertificatePem()`, immediately discarded, no real-world certificate/key
// material) was used to confirm the "not flagged" side actually parses successfully, not just assumed.
// Message uses a fixed "X509Certificate2.CreateFromPem(...)" phrase rather than embedding the argument
// text — same UX lesson already applied in RPAD0392: embedding a realistic multi-line PEM block directly
// in a diagnostic message would be unreadable.
// Extended (473-й проход, batch, candidate #1 from the thirty-sixth research subagent survey) to also
// cover the previously-excluded 2-arg `CreateFromPem(ReadOnlySpan<char> certPem, ReadOnlySpan<char>
// keyPem)` overload. Confirmed empirically with a real freshly-generated self-signed cert+key (RSA.Create
// → CertificateRequest.CreateSelfSigned → ExportCertificatePem/ExportRSAPrivateKeyPem, discarded
// immediately): an invalid `certPem` always throws `CryptographicException` (a distinct "certificate
// contents..." message) regardless of `keyPem`'s value, since cert parsing happens first.
// SELF-CAUGHT CORRECTNESS BUG (caught before shipping, not via PROBE but via re-reading the code): the
// initial design substituted a dummy real key/cert for whichever of the two arguments wasn't a literal,
// to isolate testing the other. This is safe for an INVALID certPem literal (cert parsing fails before
// any key involvement, confirmed). It is NOT safe for a keyPem literal tested against a non-literal
// certPem, or vice versa — confirmed empirically that a well-formed, syntactically-valid RSA key that
// simply doesn't MATCH the paired certificate produces the EXACT SAME "key contents..." exception message
// as a genuinely malformed key, so substituting a dummy cert to test an unrelated key literal (or a dummy
// key to test a cert literal + non-literal key) would misattribute a mismatch as an inherent flaw in the
// literal, which it may not be — the real (unknown, non-literal) counterpart might validly match at
// runtime. Final design: `certPem` must always be a literal (bails entirely otherwise); if `keyPem` is
// ALSO a literal, both are real user values and any throw is fully accurate; if `keyPem` is NOT a
// literal, only a "certificate contents..."-flavored throw is trusted (cert-only failure, independent of
// key), a "key contents..."-flavored throw against the dummy-key placeholder is NOT flagged (ambiguous).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCertificatePemLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0393";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509Certificate2.CreateFromPem argument is not a valid PEM-encoded certificate",
        messageFormat: "'X509Certificate2.CreateFromPem(...)' always throws CryptographicException at runtime — the text is not a valid PEM-encoded certificate",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509Certificate2.CreateFromPem(ReadOnlySpan<char>[, ReadOnlySpan<char>]) throws CryptographicException when certPem (or, for the 2-arg overload, keyPem) isn't a recognized PEM-encoded certificate/key (wrong label, malformed, or empty).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    // A real, freshly-generated self-signed cert+key, used ONLY as a known-valid placeholder for
    // whichever of the two arguments isn't a literal — so the probe can isolate the OTHER, literal
    // argument's validity without misattributing a parse failure to a garbage placeholder. Immediately
    // discarded/no real-world key material; lazily created once and reused across analyzer passes.
    private static readonly Lazy<(string CertPem, string KeyPem)> DummyPem = new(() =>
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=RunPadAnalyzerProbe", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
        return (cert.ExportCertificatePem(), rsa.ExportRSAPrivateKeyPem());
    });

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateFromPem" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "CreateFromPem",
                IsStatic: true,
                Parameters: [{ Type: { } firstParamType }, ..] parameters,
                ContainingType: { } containingType,
            } method)
        {
            return;
        }
        if (firstParamType.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509Certificate2") return;

        if (method.Parameters.Length == 1)
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var certArg }]) return;
            if (context.SemanticModel.GetConstantValue(certArg, ct).Value is not string certPem) return;

            try
            {
                using var cert = X509Certificate2.CreateFromPem(certPem);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
            }
        }
        else if (parameters.Length == 2 && parameters[1].Type.ToDisplayString() == "System.ReadOnlySpan<char>")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var certArg }, { Expression: var keyArg }])
                return;
            // certPem MUST be a resolvable literal: cert parsing happens before key parsing/matching, so
            // an invalid certPem always throws (a distinct "certificate contents..." message) regardless
            // of keyPem's value. When certPem isn't a literal, this analyzer can't safely conclude
            // anything and bails entirely.
            if (context.SemanticModel.GetConstantValue(certArg, ct).Value is not string certConstant) return;
            var keyConstant = context.SemanticModel.GetConstantValue(keyArg, ct).Value as string;

            try
            {
                using var cert = X509Certificate2.CreateFromPem(certConstant, keyConstant ?? DummyPem.Value.KeyPem);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // When keyPem isn't a literal, the probe used a SUBSTITUTE dummy key — a "key contents..."
                // failure there is ambiguous (could mean the dummy simply doesn't match certConstant,
                // not that any real key would fail), confirmed empirically that a well-formed-but-
                // mismatched key produces the exact same message text as a genuinely malformed one. Only
                // the "certificate contents..." message is safe to trust in that case, since cert
                // validity is independent of which key it's paired with. When keyPem IS a literal, both
                // arguments are exactly what the user wrote, so any throw is fully accurate.
                if (keyConstant is null && !ex.Message.Contains("certificate contents")) return;
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
            }
        }
    }
}
