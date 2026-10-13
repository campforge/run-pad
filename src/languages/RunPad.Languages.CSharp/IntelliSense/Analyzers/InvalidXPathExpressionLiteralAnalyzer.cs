using System.Collections.Immutable;
using System.Xml.XPath;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; third of the four
// "needs verification" candidates from the research subagent's ranked list (проход 377), confirmed
// empirically before implementing. Same "run the real validator" theme, applied to the static factory
// `System.Xml.XPath.XPathExpression.Compile(string)` (deliberately NOT the `(string,
// IXmlNamespaceResolver)` overload — same single-string-overload restraint as RPAD0374's FrameworkName and
// RPAD0352's RSA.Create(int)). Before implementing, verified a real concern: does `Compile()` require the
// top-level expression to statically resolve to a node-set (which would make almost any non-path literal,
// like "1+1" or "count(//foo)", a false "always throws")? Confirmed empirically it does NOT — `Compile()`
// happily accepts Number/String/Boolean/NodeSet-returning expressions alike; the "Expression must evaluate
// to a node-set" message that appears for genuinely malformed input (e.g. an empty string) turns out to be
// a parser fallback message for certain incomplete-syntax shapes, not a general node-set requirement.
// Confirmed empirically this is a single-exception-type situation: every malformed literal tried (empty
// string, unclosed "[", unclosed string literal, trailing operator, invalid token) throws
// `System.Xml.XPath.XPathException`, never a more specific subtype.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXPathExpressionLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0377";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XPathExpression literal is not a syntactically valid XPath expression",
        messageFormat: "'XPathExpression.Compile(...)' always throws XPathException at runtime — '{0}' is not a syntactically valid XPath expression",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The XPathExpression.Compile(string) static factory throws XPathException for a string that isn't a syntactically valid XPath expression.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Compile" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string xpath) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Compile",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.XPath.XPathExpression") return;

        try
        {
            _ = XPathExpression.Compile(xpath);
        }
        catch (XPathException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), xpath));
        }
    }
}
