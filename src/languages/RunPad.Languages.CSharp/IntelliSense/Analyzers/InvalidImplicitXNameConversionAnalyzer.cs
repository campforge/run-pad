using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (475-й проход, batch) — candidate #2 from the thirty-eighth research subagent survey,
// re-verified independently before implementing. A string literal argument that implicitly converts to
// `System.Xml.Linq.XName` (via `XName`'s own implicit conversion operator, itself just `XName.Get`
// under the hood) at an INVOCATION-ARGUMENT call site — genuinely different syntax shape from both
// already-covered cases: `InvalidXNameLiteralAnalyzer` (RPAD0386) only matches explicit `XName.Get(...)`
// calls, `InvalidXElementOrXAttributeNameLiteralAnalyzer` (RPAD0428) only matches `ObjectCreationExpression`
// (`new XElement(...)`/`new XAttribute(...)`). This covers the large remaining surface of methods
// taking an `XName` parameter — `XContainer.Element`/`.Elements`/`.Descendants`, `XNode.Ancestors`/
// `.ElementsAfterSelf`/`.ElementsBeforeSelf`, `XElement.AncestorsAndSelf`/`.Attribute`/`.Attributes`/
// `.DescendantsAndSelf`/`.SetAttributeValue`/`.SetElementValue` — confirmed empirically each throws
// IDENTICALLY to calling `XName.Get(string)` directly on the same literal (same exception types, same
// messages, same `ParamName` — "expandedName", matching the 1-arg `XName.Get` overload's own param name,
// confirming the implicit conversion operator forwards to that exact overload). Confirmed the throw
// fires EAGERLY at the call site even for lazy-`IEnumerable`-returning methods like `Elements`/
// `Descendants`/`Ancestors` (before any enumeration/`.ToList()`), so "always throws independent of
// program state" holds. Matches by the resolved PARAMETER's type being exactly `XName` (not by
// enumerating every method name), so this analyzer automatically covers any current or future BCL
// method taking an `XName` parameter — same symbol-based-matching precedent as RPAD0428, generalized to
// not require listing every containing type/method. Deliberately scoped to invocation ARGUMENTS only
// this проход — the sibling property-setter (`element.Name = "..."`) and `==`/`!=`-operator shapes were
// confirmed to behave identically but are left for a possible follow-up rather than expanding this
// analyzer's syntax-kind surface further in one pass. No `[SupportedOSPlatform]` gating, no side effects
// — pure XML-Name-grammar string validation.
// SELF-CAUGHT BUG (caught via PROBE before shipping): the initial parameter-type match compared
// `parameters[i].Type.ToDisplayString()` against the bare string `"System.Xml.Linq.XName"`, which
// silently missed every method whose `XName` parameter is NULLABLE-ANNOTATED — `Elements`/`Descendants`/
// `Ancestors`/`ElementsAfterSelf`/`ElementsBeforeSelf` all take `XName?` (null means "no filter, return
// everything"), where `ToDisplayString()` includes the trailing `?`, while `Element`/`Attribute`/
// `SetAttributeValue`/`SetElementValue` take non-nullable `XName` — confirmed via reflection/semantic-
// model inspection, not assumed. Fixed by trimming a trailing `'?'` before comparing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidImplicitXNameConversionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0517";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Argument implicitly converted to XName is not a valid expanded XML name",
        messageFormat: "'{0}' always throws {1} at runtime — '{2}' is not a valid expanded XML name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A string literal argument implicitly converted to XName (e.g. XContainer.Element/Elements/Descendants, XElement.Attribute/Attributes/SetAttributeValue/SetElementValue, XNode.Ancestors/ElementsAfterSelf/ElementsBeforeSelf) throws ArgumentException for an empty string or unbalanced \"{namespace}\" braces, or XmlException for a string that also fails plain XML Name validation.");

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
        // Explicit XName.Get(...) calls are RPAD0386/RPAD0516's territory — skip them here to avoid a
        // double-reported diagnostic for the exact same call site.
        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Get" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Parameters: { } parameters } method)
            return;

        for (var i = 0; i < invocation.ArgumentList.Arguments.Count && i < parameters.Length; i++)
        {
            // Some XName-taking parameters are the nullable-annotated `XName?` (e.g. Elements/
            // Descendants/Ancestors, where null means "no filter") — trim the trailing '?' so both the
            // nullable and non-nullable forms match (self-caught via PROBE: the naive unstripped
            // comparison silently missed every `XName?`-parameter method).
            if (parameters[i].Type.ToDisplayString().TrimEnd('?') != "System.Xml.Linq.XName") continue;
            var argExpr = invocation.ArgumentList.Arguments[i].Expression;
            if (context.SemanticModel.GetConstantValue(argExpr, ct).Value is not string expandedName) continue;

            try
            {
                _ = XName.Get(expandedName);
            }
            catch (System.Exception ex) when (ex is System.ArgumentException or System.Xml.XmlException)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), ex.GetType().Name, expandedName));
            }
            return;
        }
    }
}
