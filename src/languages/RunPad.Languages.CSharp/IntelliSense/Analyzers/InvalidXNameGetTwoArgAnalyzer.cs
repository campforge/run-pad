using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the thirty-eighth research subagent survey,
// re-verified independently before implementing. `System.Xml.Linq.XName.Get(string localName, string
// namespaceName)` — the 2-arg overload, structurally outside the already-covered 1-arg `XName.Get(string
// expandedName)` (RPAD0386, which pattern-matches a single-`string`-parameter signature only). Confirmed
// empirically: an empty `localName` throws `ArgumentException` — note `ParamName` is `"name"` here, NOT
// `"expandedName"` (the 1-arg overload's own param name) — an internal-implementation naming quirk, not
// assumed. A `localName` that fails plain XML-Name grammar (digit-start, embedded invalid characters,
// etc.) throws `System.Xml.XmlException` (`ParamName` null, message text only) — same two-exception-type
// shape as RPAD0386/RPAD0428. `namespaceName` is confirmed to NEVER be validated for any content
// (empty, garbage text, even literal `{`/`}` characters all pass through unchanged) — this 2-arg
// overload does NOT parse Clark `"{ns}name"` notation the way the 1-arg overload does, so `localName`'s
// validity is fully independent of `namespaceName`'s value (confirmed: a bad `localName` throws
// identically regardless of `namespaceName`). No `[SupportedOSPlatform]` gating, no side effects — pure
// XML-Name-grammar string validation, Unicode-category-based (not culture-dependent).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXNameGetTwoArgAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0516";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XName.Get localName argument is not a valid XML name",
        messageFormat: "'XName.Get(...)' always throws {0} at runtime — '{1}' is not a valid XML name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XName.Get(string localName, string namespaceName) throws ArgumentException for an empty localName, or XmlException for a localName that fails plain XML Name validation. namespaceName is never validated.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Get" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var localNameArg }, _]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(localNameArg, ct).Value is not string localName) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Get",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.Linq.XName") return;

        try
        {
            _ = XName.Get(localName, "urn:runpad-analyzer-probe");
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.Xml.XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, localName));
        }
    }
}
