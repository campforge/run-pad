using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #5, `System.Xml.XmlConvert.ToChar(string)` — joins
// the rest of the `XmlConvert.ToXxx` family already in this catalog (RPAD0389/0390/0391/0413/0414).
// Confirmed empirically, matching the agent's report exactly: single exception type, `FormatException`
// ("String must be exactly one character long."), thrown whenever the argument's length isn't exactly 1
// — including the empty string. A single-condition check, smaller in scope than this family's other
// members, but genuinely distinct from `System.Convert.ToChar` (a different type entirely, already
// covered by `InvalidConvertToCharArgumentAnalyzer`/`ConvertToCharOverflowAnalyzer` — confirmed via grep
// neither of those files even mention `XmlConvert`).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlCharLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0424";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.ToChar argument is not exactly one character long",
        messageFormat: "'XmlConvert.ToChar(...)' always throws FormatException at runtime — '{0}' is not exactly one character long",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.ToChar(string) throws FormatException when the argument's length isn't exactly 1.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToChar" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToChar",
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
            XmlConvert.ToChar(text);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), text));
        }
    }
}
