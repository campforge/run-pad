using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. `RSA.ImportFromPem(string)` — a REAL surprise reflection caught before
// any code was written: this method (and its X509Certificate2.CreateFromPem sibling, RPAD0393) does NOT
// actually take a `string` parameter at all — its true signature is `ImportFromPem(ReadOnlySpan<char>)`; a
// string literal argument only compiles via C#'s implicit string-to-span conversion. This is the first
// analyzer in this catalog scoped to a `ReadOnlySpan<char>` parameter rather than `SpecialType.System_
// String` — every other analyzer's parameter-type check needed adjusting for this one. Also confirmed via
// reflection, proactively (learning RPAD0352's lesson the easy way instead of the hard way this time):
// `ImportFromPem` is independently declared (hidden via `new`) on EACH concrete algorithm type — `RSA`,
// `ECAlgorithm` (ECDsa's base), etc. — not inherited unmodified from the shared `AsymmetricAlgorithm` base
// the way RPAD0352's `KeySize`/`BlockSize` properties are, so ordinary `ContainingType` resolution already
// correctly targets exactly `RSA` for a receiver whose static type is `RSA`, no receiver-expression
// workaround needed here. Scoped to `RSA` only, not `ECDsa`/`DSA`/other asymmetric algorithms — each
// expects different PEM labels, a separate validation surface not probed here (see RPAD0400, ECDsa's
// sibling). Originally shipped believing this was single-exception-type
// (`ArgumentException` for empty/garbage/wrong-label text); EXTENDED earlier after empirically
// discovering a missed case while verifying ECDsa's sibling analyzer: a PEM block that IS structurally
// recognized (a generic "PUBLIC KEY" label, correctly base64-decodable) but whose key DATA is for a
// DIFFERENT algorithm (e.g. an EC public key under the generic label) throws `System.Security.
// Cryptography.CryptographicException` instead — genuinely different from the "PEM structure itself is
// unrecognized" `ArgumentException` cases. The message format now parameterizes the exception type name,
// same technique this catalog's other multi-exception-type analyzers already use. A REAL freshly-generated
// 2048-bit RSA public key PEM (via `RSA.Create(2048).ExportSubjectPublicKeyInfoPem()`, immediately
// discarded, no real-world key material) was used to confirm the "not flagged" side actually imports
// successfully, not just assumed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRsaPemLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0392";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "RSA.ImportFromPem argument is not a valid PEM-encoded key",
        messageFormat: "'{0}.ImportFromPem(...)' always throws {1} at runtime — the text is not a valid PEM-encoded RSA key",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RSA.ImportFromPem(ReadOnlySpan<char>) throws ArgumentException for text that isn't a recognized PEM structure (wrong label, malformed, or empty), or CryptographicException for a structurally valid PEM whose key data is for a different algorithm.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ImportFromPem" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string pem) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ImportFromPem",
                IsStatic: false,
                Parameters: [{ Type: { } paramType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (paramType.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.RSA") return;

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
        }
        catch (System.Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString(), ex.GetType().Name));
        }
    }
}
