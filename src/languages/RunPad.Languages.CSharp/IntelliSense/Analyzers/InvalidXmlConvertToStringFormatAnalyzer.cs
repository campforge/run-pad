using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Xml.XmlConvert.ToString(DateTime value, string format)` /
// `XmlConvert.ToString(DateTimeOffset value, string format)` — grep-confirmed no prior coverage. Same
// "second-parameter-string, format validity is independent of the value" theme already established by
// `InvalidToStringFormatSpecifierAnalyzer` (RPAD0346) for `value.ToString(format)`: the `format` literal's
// validity doesn't depend on the specific (non-literal) `value` argument, so the analyzer probes with a
// fixed representative value (`DateTime.MinValue`/`DateTimeOffset.MinValue`) the same way RPAD0346 does,
// rather than requiring the `value` argument itself to be a literal. Confirmed empirically, single
// exception type: `FormatException` for a format string that isn't one of `XmlConvert.ToString`'s
// recognized custom/standard specifiers (e.g. `"Z"` alone, as opposed to a full round-trip specifier like
// `"s"`). Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects — pure string formatting.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlConvertToStringFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0436";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.ToString format specifier is invalid",
        messageFormat: "'XmlConvert.ToString(...)' always throws FormatException at runtime — '{0}' is not a recognized format specifier for {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.ToString(DateTime, string)/XmlConvert.ToString(DateTimeOffset, string) throw FormatException when the format string isn't a recognized specifier.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var formatArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(formatArg, ct).Value is not string format) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToString",
                IsStatic: true,
                Parameters: [{ Type: { } valueType }, { Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.XmlConvert") return;

        var typeName = valueType.ToDisplayString();
        bool threw;
        try
        {
            _ = typeName switch
            {
                "System.DateTime" => XmlConvert.ToString(DateTime.MinValue, format),
                "System.DateTimeOffset" => XmlConvert.ToString(DateTimeOffset.MinValue, format),
                _ => "",
            };
            threw = false;
        }
        catch (FormatException)
        {
            threw = true;
        }
        if (typeName is not ("System.DateTime" or "System.DateTimeOffset")) return;
        if (!threw) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), format, valueType.Name));
    }
}
