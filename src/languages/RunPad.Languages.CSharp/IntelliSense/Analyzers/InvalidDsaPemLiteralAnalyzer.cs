using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; third and final asymmetric
// algorithm covered in this PEM-import family, completing RSA (RPAD0392, проход 400, extended проход 408)
// and ECDsa/ECDiffieHellman (RPAD0400, проход 409). Confirmed via reflection that `DSA` — unlike the EC
// family, which shares `ImportFromPem` via the common `ECAlgorithm` base — independently declares its own
// `ImportFromPem`, the same shape as RSA (`DSA`'s base type is plain `AsymmetricAlgorithm`, and `DSA` hides
// the base implementation with its own). Scoped to `DSA` accordingly, matching RPAD0392's approach rather
// than RPAD0400's broader base-type scope. Confirmed empirically this is the same TWO-exception-type
// situation as its RSA/EC siblings: empty string, garbage text, and an unrecognized PEM structure throw
// `ArgumentException`, while a structurally-recognized PEM (a generic "PUBLIC KEY" label) whose key data is
// for a DIFFERENT algorithm (e.g. a real RSA public key under the generic label) throws `System.Security.
// Cryptography.CryptographicException` instead. A REAL freshly-generated DSA public key PEM (via
// `DSA.Create().ExportSubjectPublicKeyInfoPem()`, immediately discarded, no real-world key material) was
// used to confirm the "not flagged" side actually imports successfully, not just assumed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDsaPemLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0401";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DSA.ImportFromPem argument is not a valid PEM-encoded key",
        messageFormat: "'{0}.ImportFromPem(...)' always throws {1} at runtime — the text is not a valid PEM-encoded DSA key",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DSA.ImportFromPem(ReadOnlySpan<char>) throws ArgumentException for text that isn't a recognized PEM structure (wrong label, malformed, or empty), or CryptographicException for a structurally valid PEM whose key data is for a different algorithm.");

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
        if (containingType.ToDisplayString() != "System.Security.Cryptography.DSA") return;

        try
        {
            using var dsa = DSA.Create();
            dsa.ImportFromPem(pem);
        }
        catch (System.Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString(), ex.GetType().Name));
        }
    }
}
