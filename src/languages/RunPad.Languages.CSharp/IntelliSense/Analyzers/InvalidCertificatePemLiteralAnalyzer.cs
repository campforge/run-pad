using System.Collections.Immutable;
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
        description: "X509Certificate2.CreateFromPem(ReadOnlySpan<char>) throws CryptographicException for text that isn't a recognized PEM-encoded certificate (wrong label, malformed, or empty).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateFromPem" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string pem) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "CreateFromPem",
                IsStatic: true,
                Parameters: [{ Type: { } paramType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (paramType.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509Certificate2") return;

        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
    }
}
