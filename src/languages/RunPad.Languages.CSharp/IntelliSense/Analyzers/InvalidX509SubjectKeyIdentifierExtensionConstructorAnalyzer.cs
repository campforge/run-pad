using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #5 from the twenty-third research subagent survey
// (a fourth revisit of `System.Security.Cryptography.X509Certificates`, which has now produced a new
// find on every visit — X500DistinguishedNameBuilder/RPAD0472, SubjectAlternativeNameBuilder/RPAD0473,
// CertificateRevocationListBuilder/RPAD0477, and now this), re-verified independently before
// implementing. `X509SubjectKeyIdentifierExtension(string subjectKeyIdentifier, bool critical)` —
// confirmed empirically: length 0 or exactly 1 throws `ArgumentException` ("Array may not be empty or
// null"); length 2 or more NEVER throws at construction regardless of hex validity (deliberately
// NOT claimed as a hex-format check — confirmed a non-hex string like "zz" and an odd-length leftover
// like "abc" are both silently tolerated, so this analyzer only flags the length-0/1 boundary, nothing
// about content). Confirmed identical behavior across en-US/de-DE/ar-SA/ja-JP. Confirmed no
// `[SupportedOSPlatform]` gating (reflection: only nullable-context attributes) and no side effects —
// pure in-memory extension construction, no certificate ever built/signed. The sibling `byte[]`
// overload and `X509AuthorityKeyIdentifierExtension` were considered but declined — the byte[] overload
// throws `ArgumentNullException` for null (not a literal-driven case) and an empty array check that's
// harder to express as a compile-time array-length literal reliably; `X509AuthorityKeyIdentifierExtension`'s
// throw is content-dependent (ASN.1 validity), not a pure length check, so out of scope for this pattern.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509SubjectKeyIdentifierExtensionConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0483";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509SubjectKeyIdentifierExtension subjectKeyIdentifier is too short",
        messageFormat: "'new X509SubjectKeyIdentifierExtension(...)' always throws ArgumentException at runtime — subjectKeyIdentifier must be at least 2 characters",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509SubjectKeyIdentifierExtension(string, bool) throws ArgumentException when subjectKeyIdentifier is empty or a single character.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_Boolean }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension") return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var idArg }, ..] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(idArg, ct).Value is not string subjectKeyIdentifier) return;

        try
        {
            _ = new X509SubjectKeyIdentifierExtension(subjectKeyIdentifier, false);
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
        }
    }
}
