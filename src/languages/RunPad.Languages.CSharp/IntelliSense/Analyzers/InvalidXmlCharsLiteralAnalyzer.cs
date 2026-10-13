using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. `VerifyXmlChars(string)` — grep-confirmed the only prior mention
// is InvalidXmlNameLiteralAnalyzer's (RPAD0384) doc comment deferring it, not real coverage. Confirmed
// empirically this is a single-exception-type situation: a character illegal in XML 1.0 text content (NUL,
// vertical tab, form feed, bell — the C0 control characters outside the small allow-list) throws
// `XmlException`; an empty string does NOT throw (unlike `VerifyName`/`VerifyNCName`'s empty-string
// `ArgumentException` special case), consistent with `VerifyTOKEN`'s similarly lenient empty-string
// handling in RPAD0394. Also confirmed newline and tab — despite being C0 control characters themselves —
// are explicitly legal in XML 1.0 text content and do NOT throw, confirmed empirically rather than assumed
// before excluding them from the "always throws" set.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlCharsLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0398";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.VerifyXmlChars argument contains a character illegal in XML",
        messageFormat: "'XmlConvert.VerifyXmlChars(...)' always throws XmlException at runtime — the text contains a character that is illegal in XML 1.0",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.VerifyXmlChars(string) throws XmlException for text containing a C0 control character illegal in XML 1.0 (e.g. NUL, vertical tab, form feed, bell) — newline and tab are explicitly legal and do not throw.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "VerifyXmlChars" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "VerifyXmlChars",
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
            _ = XmlConvert.VerifyXmlChars(value);
        }
        catch (XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
    }
}
