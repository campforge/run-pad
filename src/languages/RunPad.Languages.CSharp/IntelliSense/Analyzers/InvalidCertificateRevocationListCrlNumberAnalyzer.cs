using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (459-й проход, batch) — candidate #1 from the twenty-second research subagent survey,
// re-verified independently before implementing. `System.Security.Cryptography.X509Certificates.
// CertificateRevocationListBuilder.Build(...)` — flagged as untried by the previous (twenty-first)
// round. IMPORTANT correctness catch during re-verification: `Build` is an INSTANCE method (`new
// CertificateRevocationListBuilder().Build(...)`), not static as the agent's prose implied — confirmed
// via reflection (zero static "Build" overloads; both are instance methods) before writing any probe
// code. Both overloads take a `System.Numerics.BigInteger crlNumber` parameter — confirmed via a real
// self-signed RSA certificate end-to-end that a negative literal (a plain `int` implicitly converted to
// `BigInteger`, still constant-folds as a boxed `int` via `GetConstantValue`, same extraction technique
// already used for `InvalidBigIntegerPowExponentAnalyzer`/RPAD0350) always throws
// `ArgumentOutOfRangeException` regardless of every other argument — the check fires before any
// signing/certificate validation. Confirmed no `[SupportedOSPlatform]` gating (reflection: only
// nullable-context attributes present) and no side effects (`Build` only returns a `byte[]`, no I/O).
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP (pure numeric sign check).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCertificateRevocationListCrlNumberAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0477";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CertificateRevocationListBuilder.Build crlNumber is negative",
        messageFormat: "'CertificateRevocationListBuilder.Build(...)' always throws ArgumentOutOfRangeException at runtime — crlNumber must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CertificateRevocationListBuilder.Build throws ArgumentOutOfRangeException when crlNumber is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Build" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != "Build") return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.CertificateRevocationListBuilder") return;

        var crlNumberIndex = -1;
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (method.Parameters[i].Type.ToDisplayString() != "System.Numerics.BigInteger") continue;
            crlNumberIndex = i;
            break;
        }
        if (crlNumberIndex < 0) return;
        if (invocation.ArgumentList.Arguments.Count <= crlNumberIndex) return;

        var arg = invocation.ArgumentList.Arguments[crlNumberIndex].Expression;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not int crlNumber) return;

        try
        {
            new CertificateRevocationListBuilder().Build(
                DummyCertificate.Value, (BigInteger)crlNumber, System.DateTimeOffset.UtcNow.AddDays(7),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (System.ArgumentOutOfRangeException ex) when (ex.ParamName == "crlNumber")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
        catch
        {
            // Any other exception means the dummy certificate/args tripped a different, unrelated
            // check first — don't misattribute it to crlNumber.
        }
    }

    private static class DummyCertificate
    {
        public static readonly X509Certificate2 Value = Create();

        private static X509Certificate2 Create()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=RunPadAnalyzerProbe", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            return request.CreateSelfSigned(System.DateTimeOffset.UtcNow.AddDays(-1), System.DateTimeOffset.UtcNow.AddDays(365));
        }
    }
}
