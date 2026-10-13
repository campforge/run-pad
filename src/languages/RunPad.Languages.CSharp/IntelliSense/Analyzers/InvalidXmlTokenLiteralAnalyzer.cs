using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Sibling of
// InvalidXmlNameLiteralAnalyzer (RPAD0384, проход 392), which deliberately deferred `VerifyTOKEN`/
// `VerifyNMTOKEN` rather than guessing their exception profile matched `VerifyName`/`VerifyNCName`'s.
// Confirmed empirically it does NOT: `VerifyTOKEN`/`VerifyNMTOKEN` throw ONLY `XmlException`, never
// `ArgumentException` — critically, an EMPTY string does not get the special-cased `ArgumentException`
// treatment `VerifyName`/`VerifyNCName` give it; `VerifyTOKEN("")` doesn't even throw at all (see below),
// and `VerifyNMTOKEN("")` throws `XmlException` like every other failure mode. The two methods also enforce
// genuinely different grammars from each other, confirmed empirically before combining them under one
// analyzer (justified the same way RPAD0384 combined Name/NCName — same exception-type profile, different
// character-class details): `VerifyTOKEN` is XML Schema's "collapsed whitespace" rule — it does NOT care
// about digits/colons/most characters at all, only about leading/trailing spaces, doubled internal spaces,
// tabs, or newlines (confirmed empirically an empty string and a string starting with a digit are both
// accepted); `VerifyNMTOKEN` is an XML Name-character-legality rule (rejects control/whitespace/`<`) but,
// UNLIKE `VerifyName`, allows a leading digit and a colon (confirmed empirically before assuming NMTOKEN's
// rules matched Name's).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlTokenLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0394";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.VerifyTOKEN/VerifyNMTOKEN argument is not valid",
        messageFormat: "'XmlConvert.{0}(...)' always throws XmlException at runtime — '{1}' is not a valid {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.VerifyTOKEN(string) throws XmlException for a string with leading/trailing/doubled whitespace, a tab, or a newline. XmlConvert.VerifyNMTOKEN(string) throws XmlException for an empty string or a string containing a character illegal in an XML Name (but allows a leading digit or colon, unlike VerifyName).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "VerifyTOKEN" or "VerifyNMTOKEN" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: var resolvedName,
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            } || resolvedName != methodName)
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.XmlConvert") return;

        try
        {
            _ = methodName == "VerifyTOKEN" ? XmlConvert.VerifyTOKEN(value) : XmlConvert.VerifyNMTOKEN(value);
        }
        catch (XmlException)
        {
            var kind = methodName == "VerifyTOKEN" ? "XML token" : "XML NMTOKEN";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, value, kind));
        }
    }
}
