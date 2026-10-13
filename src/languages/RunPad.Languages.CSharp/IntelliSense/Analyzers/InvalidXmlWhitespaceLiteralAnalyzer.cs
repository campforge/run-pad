using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #7 from the third research subagent survey (411-й проход,
// full tool access, empirically self-verified), re-verified independently before implementing. One of the
// five `XmlConvert.VerifyXxx` siblings that `InvalidXmlNameLiteralAnalyzer` (RPAD0384, проход 392)
// deliberately left unaddressed — that analyzer's own doc comment flagged `VerifyWhitespace` as
// "confirmed empirically... accept an EMPTY string without throwing... left as a separate, not-yet-
// attempted candidate" back in проход 392, now finally picked up. Confirmed empirically, matching the
// agent's report exactly: single exception type, `XmlException`, thrown only when the text contains a
// character that ISN'T XML whitespace (space, tab, CR, LF) — an empty string is lenient (accepted without
// throwing), unlike `VerifyName`/`VerifyNCName` (RPAD0384) which both reject empty strings.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlWhitespaceLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0410";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.VerifyWhitespace argument is not all XML whitespace",
        messageFormat: "'XmlConvert.VerifyWhitespace(...)' always throws XmlException at runtime — '{0}' contains a character that is not XML whitespace",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.VerifyWhitespace(string) throws XmlException for text containing any character other than space, tab, CR, or LF. An empty string is accepted without throwing.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "VerifyWhitespace" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "VerifyWhitespace",
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
            XmlConvert.VerifyWhitespace(text);
        }
        catch (XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), text));
        }
    }
}
