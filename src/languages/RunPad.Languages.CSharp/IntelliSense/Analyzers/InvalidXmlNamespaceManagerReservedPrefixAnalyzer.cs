using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the twenty-first research subagent survey,
// re-verified independently before implementing. `System.Xml.XmlNamespaceManager.AddNamespace(string
// prefix, string uri)` — a method (not a type) within the heavily-covered `System.Xml` vein that prior
// rounds evidently never probed. Confirmed empirically, both case-sensitive (uppercase "XMLNS"/"XML" do
// NOT trigger this): prefix == "xmlns" (exact case) always throws ArgumentException regardless of the
// uri argument (confirmed with a plausible uri, an empty string, and the canonical xmlns namespace uri
// itself — all three throw identically); prefix == "xml" (exact case) throws ArgumentException unless
// uri is exactly "http://www.w3.org/XML/1998/namespace" — a genuine two-joint-literal check, same shape
// precedent as `InvalidPipeOptionsConstructorAnalyzer` (RPAD0454). For the "xmlns" case the uri argument
// doesn't affect the outcome, so an unresolvable (non-constant) uri doesn't block flagging; for the
// "xml" case an unresolvable uri means "can't determine" and the call is not flagged, per the
// schema/state-dependence-avoidance discipline. Confirmed `XmlNamespaceManager` is pure in-memory
// (`NameTable`-backed), no I/O; exception fires before any side effect. Confirmed invariant across
// en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlNamespaceManagerReservedPrefixAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0475";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlNamespaceManager.AddNamespace prefix is reserved",
        messageFormat: "'XmlNamespaceManager.AddNamespace(\"{0}\", ...)' always throws ArgumentException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlNamespaceManager.AddNamespace throws ArgumentException when prefix is exactly \"xmlns\" (regardless of uri), or when prefix is exactly \"xml\" and uri is not the canonical XML namespace URI.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private const string CanonicalXmlNamespaceUri = "http://www.w3.org/XML/1998/namespace";

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "AddNamespace" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != "AddNamespace") return;
        if (containingType.ToDisplayString() != "System.Xml.XmlNamespaceManager") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var prefixArg }, { Expression: var uriArg }]) return;
        if (context.SemanticModel.GetConstantValue(prefixArg, ct).Value is not string prefixValue) return;
        if (prefixValue is not ("xmlns" or "xml")) return;

        string uriValue;
        if (context.SemanticModel.GetConstantValue(uriArg, ct) is { HasValue: true, Value: string constUri })
        {
            uriValue = constUri;
        }
        else if (prefixValue == "xmlns")
        {
            uriValue = "urn:placeholder";
        }
        else
        {
            return;
        }

        try
        {
            new XmlNamespaceManager(new NameTable()).AddNamespace(prefixValue, uriValue);
        }
        catch (System.ArgumentException)
        {
            var reason = prefixValue == "xmlns"
                ? "the prefix \"xmlns\" is reserved for use by XML"
                : $"the prefix \"xml\" can only be mapped to \"{CanonicalXmlNamespaceUri}\"";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), prefixValue, reason));
        }
    }
}
