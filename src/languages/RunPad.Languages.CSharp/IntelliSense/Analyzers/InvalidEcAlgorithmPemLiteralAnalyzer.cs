using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; sibling of RPAD0392's
// RSA.ImportFromPem, deferred there explicitly ("Scoped to RSA only, not ECDsa/DSA/other asymmetric
// algorithms — each expects different PEM labels, a separate validation surface not probed here").
// Confirmed via reflection BEFORE writing any matching code (applying RPAD0392's own lesson proactively):
// unlike RSA (which independently declares its own `ImportFromPem`), `ECDsa.ImportFromPem` is declared on
// the SHARED intermediate base class `System.Security.Cryptography.ECAlgorithm` — and confirmed empirically
// that `ECDiffieHellman` (the OTHER concrete type deriving from `ECAlgorithm`) shares the exact same
// `ImportFromPem` implementation with identical exception behavior for identical inputs. So this analyzer
// scopes to `ContainingType == "System.Security.Cryptography.ECAlgorithm"` directly rather than to `ECDsa`
// specifically — a deliberately broader, empirically-justified scope that correctly and uniformly covers
// BOTH `ECDsa` and `ECDiffieHellman` receivers with one analyzer, unlike RSA which genuinely needed its own
// separate one. Confirmed empirically this is a TWO-exception-type situation, the same shape discovered for
// RSA's sibling while researching this very analyzer (which led to the проход 408 fix of RPAD0392): empty
// string, garbage text, and an unrecognized PEM structure throw `ArgumentException`, while a
// structurally-recognized PEM (a generic "PUBLIC KEY" label) whose key data is for a DIFFERENT algorithm
// (e.g. a real RSA public key under the generic label) throws `System.Security.Cryptography.
// CryptographicException` instead. A REAL freshly-generated EC public key PEM (via
// `ECDsa.Create().ExportSubjectPublicKeyInfoPem()`, immediately discarded, no real-world key material) was
// used to confirm the "not flagged" side actually imports successfully, not just assumed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidEcAlgorithmPemLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0400";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ECDsa/ECDiffieHellman.ImportFromPem argument is not a valid PEM-encoded key",
        messageFormat: "'{0}.ImportFromPem(...)' always throws {1} at runtime — the text is not a valid PEM-encoded EC key",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ECDsa/ECDiffieHellman.ImportFromPem(ReadOnlySpan<char>) throws ArgumentException for text that isn't a recognized PEM structure (wrong label, malformed, or empty), or CryptographicException for a structurally valid PEM whose key data is for a different algorithm.");

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
        if (containingType.ToDisplayString() != "System.Security.Cryptography.ECAlgorithm") return;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
        }
        catch (System.Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString(), ex.GetType().Name));
        }
    }
}
