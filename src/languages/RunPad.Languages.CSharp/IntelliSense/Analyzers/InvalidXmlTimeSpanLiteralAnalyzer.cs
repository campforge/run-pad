using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. `XmlConvert.ToTimeSpan(string)` —
// grep-confirmed no prior coverage. Thematically adjacent to, but genuinely NOT overlapping with,
// InvalidDateOrTimeParseLiteralAnalyzer's `TimeSpan.Parse` coverage (RPAD0365) — same class of
// legitimate coexistence already established for ContentType/MediaTypeHeaderValue and
// DateTime.Parse/SqlDateTime.Parse (RPAD0388) in this catalog. Confirmed empirically the two
// TimeSpan-parsing APIs use ENTIRELY DIFFERENT grammars: `XmlConvert.ToTimeSpan` requires the W3C XSD
// *duration* format ("P1DT2H3M4S", "-P1D"), while even the CANONICAL .NET TimeSpan format
// ("1.02:03:04", the exact string `TimeSpan.ToString()` itself produces) is REJECTED by
// `XmlConvert.ToTimeSpan` — confirmed empirically before assuming any overlap in accepted literal sets, not
// just different scoping. Single exception type: every malformed literal tried throws `FormatException`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlTimeSpanLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0390";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.ToTimeSpan argument is not a valid XSD duration literal",
        messageFormat: "'XmlConvert.ToTimeSpan(...)' always throws FormatException at runtime — '{0}' is not a valid XSD duration literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.ToTimeSpan(string) throws FormatException for a string that isn't a valid W3C XSD duration — a different grammar from TimeSpan.Parse, which it does not accept even in its own canonical format.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToTimeSpan" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToTimeSpan",
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
            _ = XmlConvert.ToTimeSpan(value);
        }
        catch (System.FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
        }
    }
}
