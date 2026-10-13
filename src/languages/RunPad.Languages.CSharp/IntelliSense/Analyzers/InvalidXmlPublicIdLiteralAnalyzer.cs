using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #8 from the third research subagent survey (411-й проход,
// full tool access, empirically self-verified), re-verified independently before implementing. The other
// `XmlConvert.VerifyXxx` sibling `InvalidXmlNameLiteralAnalyzer` (RPAD0384, проход 392) deliberately left
// unaddressed alongside `VerifyWhitespace` (RPAD0410, проход 419) — that analyzer's own doc comment flagged
// `VerifyPublicId` as "confirmed empirically... accept an EMPTY string without throwing... left as a
// separate, not-yet-attempted candidate". Confirmed empirically, matching the agent's report exactly:
// single exception type, `XmlException`, thrown for a character outside the XML PUBLIC ID literal grammar
// (a fairly restrictive whitelist — letters, digits, and a small set of punctuation; notably '<', '{',
// tab, and backslash are all rejected). Both an empty string AND an embedded newline are lenient (accepted
// without throwing) — genuinely surprising for the newline case, since PUBLIC ID values are normally
// single-line SGML/XML tokens, but confirmed via direct reflection-backed test, not assumed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlPublicIdLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0411";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.VerifyPublicId argument contains a character outside the PUBLIC ID literal grammar",
        messageFormat: "'XmlConvert.VerifyPublicId(...)' always throws XmlException at runtime — '{0}' contains a character that is not valid in an XML PUBLIC ID literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.VerifyPublicId(string) throws XmlException for text containing a character outside the XML PUBLIC ID literal grammar. Both an empty string and embedded newlines are accepted without throwing.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "VerifyPublicId" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "VerifyPublicId",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.XmlConvert") return;

        try
        {
            XmlConvert.VerifyPublicId(text);
        }
        catch (XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), text));
        }
    }
}
