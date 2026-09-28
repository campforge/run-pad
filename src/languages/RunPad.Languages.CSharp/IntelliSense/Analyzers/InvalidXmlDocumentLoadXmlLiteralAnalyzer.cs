using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (425-й проход, batch) — candidate #1 from the fourth research subagent survey (424-й
// проход, full tool access, empirically self-verified), re-verified independently before implementing.
// `XmlDocument.LoadXml(string)` — an INSTANCE method sibling of the already-covered static
// `XDocument.Parse`/`XElement.Parse` (RPAD0257, проход 263), whose own doc comment explicitly scopes to
// those two types' 1-arg overloads only, never mentioning `XmlDocument`. Same well-formedness grammar,
// same exception. Confirmed empirically, matching the agent's report exactly: single exception type,
// `XmlException`, for empty text ("Root element is missing"), garbage text, mismatched start/end tags,
// multiple root elements, and an undeclared namespace prefix. Confirmed no false positives on valid XML
// including an XML declaration, a comment, and CDATA. Culture-independent, pure in-memory (DTDs are not
// fetched by default, confirmed for the sibling analyzer already and unchanged for `XmlDocument`). Unlike
// every other analyzer in this catalog, this one matches an INSTANCE method call (`doc.LoadXml(literal)`)
// rather than a static one — `IsStatic: false` instead of `true`, with the receiver's static type left
// unconstrained (any expression whose type is `XmlDocument` works, matching the same receiver-agnostic
// approach already used for `ImportFromPem`-family instance methods, RPAD0392/0400/0401/0403/0404/0405).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlDocumentLoadXmlLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0415";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlDocument.LoadXml argument is not well-formed XML",
        messageFormat: "'{0}.LoadXml(...)' always throws XmlException at runtime — this literal is not well-formed XML",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlDocument.LoadXml(string) throws XmlException for a string that isn't well-formed XML.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "LoadXml" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "LoadXml",
                IsStatic: false,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.XmlDocument") return;

        try
        {
            new XmlDocument().LoadXml(value);
        }
        catch (XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString()));
        }
    }
}
