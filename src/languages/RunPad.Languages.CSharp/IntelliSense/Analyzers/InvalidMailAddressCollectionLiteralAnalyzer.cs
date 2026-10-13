using System.Collections.Immutable;
using System.Net.Mail;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; last candidate from the
// second research subagent survey (проход 397) — flagged by the agent as "low-medium confidence" with the
// explicit caveat that its rules "may turn out identical" to the already-shipped `new MailAddress(string)`
// coverage (InvalidMailAddressLiteralAnalyzer, RPAD0250, проход 256), so checked for genuine non-duplication
// before implementing (learning from проход 370's near-miss). Confirmed empirically it is NOT a duplicate:
// `MailAddressCollection.Add(string)` accepts a COMMA-SEPARATED list of addresses — a call shape `new
// MailAddress(string)` cannot represent at all (confirmed empirically that `new MailAddress("a@b.com,
// c@d.com")` does NOT throw, unlike `collection.Add("a@b.com, c@d.com")` which succeeds for a different
// reason — it actually adds two separate addresses to the collection, versus MailAddress silently accepting
// the whole string as some single address representation). Confirmed empirically this is genuinely a
// TWO-exception-type situation: an empty string throws `ArgumentException`, while a malformed single
// address or a malformed entry anywhere within a comma-separated list (including an empty entry between
// commas) throws `FormatException` — the message format parameterizes the exception type name. Scoped to
// the `Add(string)` overload only, not the sibling `Add(MailAddress)` overload (a different, non-literal-
// string parameter shape entirely, confirmed via reflection there are exactly two overloads).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMailAddressCollectionLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0397";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MailAddressCollection.Add argument is not a valid address list",
        messageFormat: "'{0}.Add(...)' always throws {1} at runtime — '{2}' is not a valid comma-separated email address list",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MailAddressCollection.Add(string) throws ArgumentException for an empty string, or FormatException for a string containing a malformed or empty address entry.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string addresses) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Add",
                IsStatic: false,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Net.Mail.MailAddressCollection") return;

        try
        {
            new MailAddressCollection().Add(addresses);
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString(), ex.GetType().Name, addresses));
        }
    }
}
