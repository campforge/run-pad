using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (четырёхсотый проход, batch) — own pick, not a Roslynator port; fourth candidate from the
// second research subagent survey (проход 397, read-only tools, doc-based hunches only), re-verified
// empirically before implementing. `RSA.ImportFromPem(string)` — a REAL surprise reflection caught before
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
// expects different PEM labels, a separate validation surface not probed here. Confirmed empirically this
// is a single-exception-type situation: empty string, garbage text, an unlabeled PEM block, and a
// wrong-label PEM block (e.g. "CERTIFICATE" instead of a key label) all throw `ArgumentException`. A REAL
// freshly-generated 2048-bit RSA public key PEM (via `RSA.Create(2048).ExportSubjectPublicKeyInfoPem()`,
// immediately discarded, no real-world key material) was used to confirm the "not flagged" side actually
// imports successfully, not just assumed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRsaPemLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0392";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "RSA.ImportFromPem argument is not a valid PEM-encoded key",
        messageFormat: "'{0}.ImportFromPem(...)' always throws ArgumentException at runtime — the text is not a valid PEM-encoded RSA key",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RSA.ImportFromPem(ReadOnlySpan<char>) throws ArgumentException for text that isn't a recognized PEM-encoded RSA key (wrong label, malformed, or empty).");

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
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString()));
        }
    }
}
