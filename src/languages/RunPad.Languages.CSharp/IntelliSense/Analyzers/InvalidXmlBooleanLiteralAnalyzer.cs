using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста девяносто седьмой проход, batch) — own pick, not a Roslynator port; sourced from a
// second research subagent survey (проход 397), which this time ran with read-only tools and could NOT
// execute its own verification scripts — every candidate it returned was explicitly flagged as an
// "unverified hunch" based only on reading XML doc comments, not empirical confirmation. Re-verified
// empirically before implementing, per this session's standing discipline. `XmlConvert.ToBoolean(string)` —
// grep-confirmed the only prior XmlConvert coverage is InvalidXmlNameLiteralAnalyzer (RPAD0384, unrelated
// VerifyName/VerifyNCName methods). GENUINE SURPRISE confirmed empirically: unlike
// InvalidBooleanLiteralAnalyzer's `bool.Parse`/`Convert.ToBoolean` (RPAD0253, case-INSENSITIVE, only
// "True"/"False"), `XmlConvert.ToBoolean` is case-SENSITIVE and only accepts LOWERCASE "true"/"false" or
// "1"/"0" — it REJECTS "True"/"TRUE" (which RPAD0253's pair happily accepts) and ACCEPTS "1"/"0" (which
// RPAD0253's pair rejects). The two analyzers are near-complementary on their accepted literal sets, not
// just differently-scoped — confirmed empirically before assuming any overlap risk. Single exception type:
// every malformed literal tried throws `FormatException`. Also confirmed leniency: surrounding whitespace
// is trimmed before comparison ("  true  " parses fine).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlBooleanLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0389";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.ToBoolean argument is not a valid XML boolean literal",
        messageFormat: "'XmlConvert.ToBoolean(...)' always throws FormatException at runtime — '{0}' is not a valid XML boolean literal (expected lowercase \"true\"/\"false\" or \"1\"/\"0\")",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.ToBoolean(string) throws FormatException for a string other than lowercase \"true\"/\"false\" or \"1\"/\"0\" — it is case-sensitive, unlike bool.Parse.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToBoolean" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToBoolean",
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
            _ = XmlConvert.ToBoolean(value);
        }
        catch (System.FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
        }
    }
}
