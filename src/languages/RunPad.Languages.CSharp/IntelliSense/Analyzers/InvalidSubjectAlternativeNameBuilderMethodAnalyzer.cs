using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (459-й проход, batch) — candidate #1 from the twenty-first research subagent survey,
// re-verified independently before implementing. `System.Security.Cryptography.X509Certificates.
// SubjectAlternativeNameBuilder` — a sibling of the already-covered `X500DistinguishedNameBuilder`
// (RPAD0472), same namespace, distinct type. `AddDnsName(string)`/`AddEmailAddress(string)`/
// `AddUserPrincipalName(string)` all throw `ArgumentOutOfRangeException` (NOT `ArgumentException` — a
// discoverable, non-obvious choice, confirmed via the exact runtime type) for the empty string only.
// Confirmed otherwise fully lenient — no format validation at all (`"exa mple .com!!"`, `"notanemail"`,
// `"!!! not upn"` are all accepted unchanged). The other two `Add*` methods (`AddUri(Uri)`,
// `AddIpAddress(IPAddress)`) don't take a literal-friendly type and are out of scope. Confirmed no
// `[SupportedOSPlatform]` gating (reflection: only nullable-context attributes present) and no side
// effects — pure in-memory ASN.1 buffer building, same internals already accepted for
// `X500DistinguishedNameBuilder`. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSubjectAlternativeNameBuilderMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0473";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SubjectAlternativeNameBuilder method argument is empty",
        messageFormat: "'SubjectAlternativeNameBuilder.{0}(\"\")' always throws ArgumentOutOfRangeException at runtime — the value must not be empty",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SubjectAlternativeNameBuilder.AddDnsName/AddEmailAddress/AddUserPrincipalName throw ArgumentOutOfRangeException for an empty string.");

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
        if (methodName is not ("AddDnsName" or "AddEmailAddress" or "AddUserPrincipalName")) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var firstArg }]) return;
        if (context.SemanticModel.GetConstantValue(firstArg, ct).Value is not string value) return;

        var builder = new SubjectAlternativeNameBuilder();
        try
        {
            switch (methodName)
            {
                case "AddDnsName": builder.AddDnsName(value); break;
                case "AddEmailAddress": builder.AddEmailAddress(value); break;
                default: builder.AddUserPrincipalName(value); break;
            }
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
        }
    }
}
