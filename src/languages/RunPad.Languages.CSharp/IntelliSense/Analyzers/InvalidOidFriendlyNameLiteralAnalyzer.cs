using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста девяносто пятый проход, batch) — own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379-RPAD0386. `new Oid(string)` was also considered and
// DROPPED — confirmed empirically it never throws for any string; it's a lenient data holder that accepts
// literally any value into its `Value` field without validating it as a real dotted-decimal OID, unlike
// this catalog's other format-validating candidates. `Oid.FromFriendlyName(string, OidGroup)` is a
// genuinely different, throw-worthy API: it's the only overload (confirmed via reflection, no
// `[StringSyntax]` attribute on either parameter), and it looks the friendly name up against the real OID
// database rather than just storing text. Confirmed empirically it throws `CryptographicException`
// (single exception type) for both an empty string and any friendly name not present in the database (e.g.
// "sha256" resolves, "not-a-real-friendly-name" doesn't). Same "most permissive validator" reasoning
// already established for RPAD0376/IdnMapping: always probes with `OidGroup.All` (the broadest search)
// regardless of which `OidGroup` the user's own call site passes — if the name isn't found even under the
// broadest group, it can't be found under a narrower one either, so this can only under-report (miss a
// real throw for a narrower group), never over-report a false positive.
// Extended (474-й проход, batch, candidate #2 from the thirty-seventh research subagent survey) to also
// cover the sibling static method `Oid.FromOidValue(string oidValue, OidGroup group)` — same database
// lookup mechanism (confirmed empirically it's NOT a syntax validator: well-formed dotted-decimal
// strings like "1.2.3"/"9.9.9" that aren't real registered OIDs throw identically to garbage text or an
// empty string, while genuinely registered-but-not-famous OIDs like "1.3.14.3.2.26" (SHA1) succeed),
// same single exception type (`CryptographicException`), same `OidGroup.All`-broadest-search safety
// reasoning (re-verified independently: `FromOidValue("2.5.4.3", OidGroup.HashAlgorithm)` for a
// commonName OID still succeeds — the group scopes but doesn't filter/reject).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidOidFriendlyNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0387";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Oid.FromFriendlyName/FromOidValue argument is not a recognized OID",
        // {0}=method name, {1}=the literal, {2}="friendly name"/"value" — for FromFriendlyName this
        // formats byte-for-byte identically to the original hardcoded-FromFriendlyName template, so no
        // existing permanent test needed updating when FromOidValue was added.
        messageFormat: "'Oid.{0}(...)' always throws CryptographicException at runtime — '{1}' is not a recognized OID {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Oid.FromFriendlyName(string, OidGroup) and Oid.FromOidValue(string, OidGroup) throw CryptographicException for an empty string or a name/value that doesn't match any known OID, even under the broadest OidGroup.All search.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        var isFromOidValue = methodName == "FromOidValue";
        if (!isFromOidValue && methodName != "FromFriendlyName") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var nameArg }, _]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string literalValue) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, _],
                ContainingType: { } containingType,
            } method)
        {
            return;
        }
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.Oid") return;

        try
        {
            if (isFromOidValue) _ = Oid.FromOidValue(literalValue, OidGroup.All);
            else _ = Oid.FromFriendlyName(literalValue, OidGroup.All);
        }
        catch (CryptographicException)
        {
            var kind = isFromOidValue ? "value" : "friendly name";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, literalValue, kind));
        }
    }
}
