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
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidOidFriendlyNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0387";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Oid.FromFriendlyName argument is not a recognized OID friendly name",
        messageFormat: "'Oid.FromFriendlyName(...)' always throws CryptographicException at runtime — '{0}' is not a recognized OID friendly name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Oid.FromFriendlyName(string, OidGroup) throws CryptographicException for an empty string or a name that doesn't match any known OID, even under the broadest OidGroup.All search.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FromFriendlyName" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var nameArg }, _]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string friendlyName) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "FromFriendlyName",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, _],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.Oid") return;

        try
        {
            _ = Oid.FromFriendlyName(friendlyName, OidGroup.All);
        }
        catch (CryptographicException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), friendlyName));
        }
    }
}
