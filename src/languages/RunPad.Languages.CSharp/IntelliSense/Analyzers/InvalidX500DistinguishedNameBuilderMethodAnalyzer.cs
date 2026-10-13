using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidates #1/#2/#3 from the twentieth research subagent survey,
// re-verified independently before implementing. `System.Security.Cryptography.X509Certificates.
// X500DistinguishedNameBuilder` — a genuinely fresh type distinct from the already-covered
// `X500DistinguishedName` constructor (RPAD0382); bundled several call shapes into one analyzer, same
// "several methods, one diagnostic" precedent as `InvalidHashAlgorithmNameLiteralAnalyzer` (RPAD0426).
// Confirmed empirically, all throw `System.ArgumentException`: `Add(string oidValue, string value,
// UniversalTagNumber? tagNumber = null)` for a malformed/empty `oidValue` (scoped to the single-arc/
// non-numeric/first-arc-out-of-0-2-range shapes already confirmed for `AsnWriter.WriteObjectIdentifier`,
// RPAD0468, since this forwards to the same OID parser internally); `AddCountryOrRegion(string
// twoLetterCode)` for anything other than exactly two A-Z letters (confirmed case-INSENSITIVE — `"us"`
// lowercase is valid, only length/character-class matters); and the seven `Add*`/`AddEmailAddress`
// methods (`AddCommonName`/`AddLocalityName`/`AddOrganizationName`/`AddOrganizationalUnitName`/
// `AddStateOrProvinceName`/`AddDomainComponent`/`AddEmailAddress`) for the EMPTY STRING specifically —
// confirmed `AddEmailAddress` is otherwise fully lenient (no email-format validation at all, `"not-an-
// email"` is accepted). Confirmed no `[SupportedOSPlatform]` gating (reflection: only nullable-context
// attributes present) and no side effects — pure in-memory ASN.1 buffer building, same internals already
// accepted for `System.Formats.Asn1` (проход 457). Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
// Deliberately excludes the sibling `Add(Oid oid, string value, ...)` overload — `Oid` isn't a
// literal-friendly type, same single-string-overload restraint used elsewhere in this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidX500DistinguishedNameBuilderMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0472";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "X500DistinguishedNameBuilder method argument always throws",
        messageFormat: "'X500DistinguishedNameBuilder.{0}(...)' always throws ArgumentException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "X500DistinguishedNameBuilder.Add throws for a malformed/empty OID, AddCountryOrRegion throws unless given exactly two A-Z letters, and the AddCommonName/AddLocalityName/AddOrganizationName/AddOrganizationalUnitName/AddStateOrProvinceName/AddDomainComponent/AddEmailAddress methods all throw for an empty string.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> EmptyStringOnlyMethods = ImmutableHashSet.Create(
        "AddCommonName", "AddLocalityName", "AddOrganizationName", "AddOrganizationalUnitName",
        "AddStateOrProvinceName", "AddDomainComponent", "AddEmailAddress");

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
        var isAdd = methodName == "Add";
        var isAddCountry = methodName == "AddCountryOrRegion";
        var isEmptyOnly = EmptyStringOnlyMethods.Contains(methodName);
        if (!isAdd && !isAddCountry && !isEmptyOnly) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.X500DistinguishedNameBuilder") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var firstArg }, ..]) return;
        if (context.SemanticModel.GetConstantValue(firstArg, ct).Value is not string value) return;

        var builder = new X500DistinguishedNameBuilder();
        try
        {
            if (isAdd) builder.Add(value, "v");
            else if (isAddCountry) builder.AddCountryOrRegion(value);
            else InvokeEmptyOnlyMethod(builder, methodName, value);
        }
        catch (System.ArgumentException)
        {
            var reason = isAdd ? $"'{value}' is not a valid dotted-decimal OID"
                : isAddCountry ? $"'{value}' is not exactly two A-Z letters"
                : $"the value is empty";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, reason));
        }
    }

    private static void InvokeEmptyOnlyMethod(X500DistinguishedNameBuilder builder, string methodName, string value)
    {
        switch (methodName)
        {
            case "AddCommonName": builder.AddCommonName(value); break;
            case "AddLocalityName": builder.AddLocalityName(value); break;
            case "AddOrganizationName": builder.AddOrganizationName(value); break;
            case "AddOrganizationalUnitName": builder.AddOrganizationalUnitName(value); break;
            case "AddStateOrProvinceName": builder.AddStateOrProvinceName(value); break;
            case "AddDomainComponent": builder.AddDomainComponent(value); break;
            default: builder.AddEmailAddress(value); break;
        }
    }
}
