using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// DSA sibling of InvalidRsaEncryptedPemLiteralAnalyzer (RPAD0403). Confirmed via
// reflection that `DSA`, like `RSA`, independently declares its own `ImportFromEncryptedPem`
// rather than sharing it through a common base — same shape as the plain-PEM RSA/DSA split (RPAD0392/
// RPAD0401). Scoped to ONLY the `input` argument for the same reason as RPAD0403: a malformed/empty
// `input` always throws `ArgumentException` regardless of `password`, while a wrong password on an
// otherwise well-formed encrypted PEM throws `CryptographicException` instead — a relationship between
// two arguments, not an intrinsic defect in a single literal, so that branch is deliberately swallowed
// rather than reported.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDsaEncryptedPemLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0404";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DSA.ImportFromEncryptedPem input argument is not a valid PEM-encoded key",
        messageFormat: "'{0}.ImportFromEncryptedPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded (encrypted) DSA key",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DSA.ImportFromEncryptedPem(ReadOnlySpan<char>, ...) throws ArgumentException for an `input` that isn't a recognized PEM structure (wrong label, malformed, or empty), independent of the password argument.");

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
        if (containingType.ToDisplayString() != "System.Security.Cryptography.DSA") return;

        try
        {
            using var dsa = DSA.Create();
            dsa.ImportFromEncryptedPem(pem, "placeholder-password");
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
