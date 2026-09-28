using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста девяностый проход, batch) — own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379-RPAD0381 — grep-confirmed no prior coverage before
// implementing (RPAD0371's HttpHeaderValue family and RPAD0373's ContentType are unrelated namespaces).
// Same "run the real validator" theme, applied to `new X500DistinguishedName(string)` — one of six
// constructor overloads (confirmed via reflection); deliberately NOT the `(string,
// X500DistinguishedNameFlags)` 2-arg overload, byte[]/ReadOnlySpan&lt;byte&gt;/AsnEncodedData/copy overloads
// (same single-string-overload restraint as this catalog's other analyzers). Confirmed empirically this is
// a single-exception-type situation: every malformed distinguished-name string tried (no "key=value"
// structure at all, a key with no value like "=novalue", an empty attribute from a leading or doubled
// comma, or an attribute key not in .NET's known-OID table like "UNKNOWNKEY=test") throws
// `System.Security.Cryptography.CryptographicException`. Also confirmed the parser is lenient in ways
// worth noting for correct test literals: a completely empty string, an empty VALUE ("CN="), and a
// trailing comma ("CN=test,") are all accepted without throwing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX500DistinguishedNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0382";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X500DistinguishedName literal is not a valid distinguished name string",
        messageFormat: "'new X500DistinguishedName(...)' always throws CryptographicException at runtime — '{0}' is not a valid X.500 distinguished name string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The X500DistinguishedName(string) constructor throws CryptographicException for a string that isn't a well-formed \"key=value, ...\" distinguished name, or that uses an unrecognized attribute key.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ct = context.CancellationToken;
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var nameArg }] }) return;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string name) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
            } method)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X500DistinguishedName") return;

        try
        {
            _ = new X500DistinguishedName(name);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), name));
        }
    }
}
