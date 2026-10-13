using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `RSA.ImportFromEncryptedPem(ReadOnlySpan<char> input, ReadOnlySpan<char>/ReadOnlySpan<byte> password)`
// — same "run the real validator" theme as InvalidRsaPemLiteralAnalyzer's plain ImportFromPem (RPAD0392),
// but deliberately scoped to ONLY the `input` argument. Confirmed empirically: a malformed/empty `input`
// always throws `ArgumentException`, regardless of what `password` is (even the empty string or a
// correct-looking one) — the input's PEM structure is parsed before the password is ever used to decrypt
// it. A wrong password on an otherwise well-formed encrypted PEM throws `CryptographicException` instead,
// but that's a relationship between TWO arguments, not an intrinsic defect in a single literal — a
// hardcoded "wrong" password in real code isn't necessarily a bug (e.g. deliberately testing failure
// paths), so this analyzer only validates `input` (with a fixed placeholder password) and only reports the
// `ArgumentException` branch, never `CryptographicException`. Confirmed via reflection: unlike
// ImportFromPem, `RSA`/`DSA` each still independently declare their own `ImportFromEncryptedPem` (not
// shared through a common base) — see InvalidDsaEncryptedPemLiteralAnalyzer for DSA's sibling, and
// InvalidEcAlgorithmEncryptedPemLiteralAnalyzer for the EC family (shared via `ECAlgorithm`, same split as
// RPAD0400).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRsaEncryptedPemLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0403";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "RSA.ImportFromEncryptedPem input argument is not a valid PEM-encoded key",
        messageFormat: "'{0}.ImportFromEncryptedPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded (encrypted) RSA key",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RSA.ImportFromEncryptedPem(ReadOnlySpan<char>, ...) throws ArgumentException for an `input` that isn't a recognized PEM structure (wrong label, malformed, or empty), independent of the password argument.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ImportFromEncryptedPem" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var inputArg }, _]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(inputArg, ct).Value is not string pem) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ImportFromEncryptedPem",
                IsStatic: false,
                Parameters: [{ Type.SpecialType: SpecialType.None }, _],
                ContainingType: { } containingType,
            } method)
        {
            return;
        }
        if (method.Parameters[0].Type.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.RSA") return;

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromEncryptedPem(pem, "placeholder-password");
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString()));
        }
        catch (CryptographicException)
        {
            // A well-formed encrypted PEM with a password mismatch — not an intrinsic defect in `input`.
        }
    }
}
