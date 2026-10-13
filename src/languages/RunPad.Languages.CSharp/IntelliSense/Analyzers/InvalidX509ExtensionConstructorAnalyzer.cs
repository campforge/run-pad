using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the thirty-first research subagent survey,
// re-verified independently before implementing. An eleventh productive revisit of
// `X509Certificates` (after RPAD0472/0473/0477/0483/0485/0488/0496/0500). `X509Extension(string oid,
// byte[] rawData, bool critical)` — the BASE extension type, distinct from the already-covered derived
// `X509BasicConstraintsExtension`/`X509SubjectKeyIdentifierExtension` (confirmed via grep — no existing
// hit for `X509Extension` itself). Confirmed empirically: an empty-string `oid` literal throws
// `ArgumentException` ("The value cannot be an empty string.", param `oid.Value`) — confirmed invariant
// across en-US/de-DE/ar-SA/ja-JP; confirmed neither `rawData` nor `critical` affects this outcome. Noted
// but NOT exploited: a `null` literal `oid` throws `ArgumentNullException` with param name
// `'friendlyName'` (not `'oid'`) — a genuine property/parameter-name mismatch from the constructor
// internally forwarding to an `Oid`-parsing path; out of scope here since `null` isn't the empty-string
// case this analyzer targets. No `[SupportedOSPlatform]` gating, no side effects — pure in-memory
// OID/ASN.1 handling, same category as `X500DistinguishedNameBuilder`/`SubjectAlternativeNameBuilder`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX509ExtensionConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0507";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X509Extension oid is empty",
        messageFormat: "'new X509Extension(\"\", ...)' always throws ArgumentException at runtime — oid must not be an empty string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X509Extension(string oid, byte[] rawData, bool critical) throws ArgumentException when oid is the empty string.");

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
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, _, { Type.SpecialType: SpecialType.System_Boolean }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X509Extension") return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var oidArg }, ..] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(oidArg, ct).Value is not string oid) return;
        if (oid.Length != 0) return;

        try
        {
            _ = new X509Extension(oid, [], false);
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
        }
    }
}
