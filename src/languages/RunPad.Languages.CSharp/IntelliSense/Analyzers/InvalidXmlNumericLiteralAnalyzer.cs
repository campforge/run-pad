using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// The eleven numeric
// `XmlConvert.ToXxx(string)` wrappers (`ToInt16`/`ToInt32`/`ToInt64`/`ToUInt16`/`ToUInt32`/`ToUInt64`/
// `ToByte`/`ToSByte`/`ToSingle`/`ToDouble`/`ToDecimal`) — the rest of the `XmlConvert.ToXxx` family, whose
// non-numeric siblings (`ToChar`/`ToBoolean`/`ToDateTime*`/`ToTimeSpan`/`ToGuid`) are already covered
// (RPAD0389/0390/0391/0413/0414/0424). Confirmed via grep these eleven were never picked up — the doc
// comment on `InvalidXmlGuidLiteralAnalyzer` (RPAD0413) explicitly noted them as deliberately
// skipped for being "likely low novelty" versus this catalog's plain numeric Parse coverage, but that
// assumption was never independently verified until now. Confirmed empirically, matching the agent's
// report exactly: TWO exception types — `FormatException` for non-numeric text, `OverflowException` for a
// well-formed but out-of-range digit run — and confirmed XML's numeric literal grammar is more lenient
// than plain `int.Parse` in ways worth noting: leading/trailing whitespace and a leading `+` sign are both
// tolerated without throwing, and `ToDouble`/`ToSingle` accept the XML-invariant tokens `"INF"`/`"-INF"`/
// `"NaN"`. One analyzer covers all eleven via a method-name-keyed dispatch, matching this catalog's
// established multi-type-dispatch precedent (e.g. RPAD0356 for numeric `Parse`, RPAD0371 for the HTTP
// header value family) rather than eleven near-identical analyzer classes.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlNumericLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0425";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert numeric ToXxx argument is not a valid XSD numeric literal",
        messageFormat: "'XmlConvert.{0}(...)' always throws {1} at runtime — '{2}' is not a valid XSD numeric literal for this type",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The numeric XmlConvert.ToXxx(string) methods throw FormatException for non-numeric text, or OverflowException for a well-formed but out-of-range value.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethodNames = ImmutableHashSet.Create(
        "ToInt16", "ToInt32", "ToInt64", "ToUInt16", "ToUInt32", "ToUInt64",
        "ToByte", "ToSByte", "ToSingle", "ToDouble", "ToDecimal");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!TargetMethodNames.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
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
            _ = methodName switch
            {
                "ToInt16" => (object)XmlConvert.ToInt16(text),
                "ToInt32" => XmlConvert.ToInt32(text),
                "ToInt64" => XmlConvert.ToInt64(text),
                "ToUInt16" => XmlConvert.ToUInt16(text),
                "ToUInt32" => XmlConvert.ToUInt32(text),
                "ToUInt64" => XmlConvert.ToUInt64(text),
                "ToByte" => XmlConvert.ToByte(text),
                "ToSByte" => XmlConvert.ToSByte(text),
                "ToSingle" => XmlConvert.ToSingle(text),
                "ToDouble" => XmlConvert.ToDouble(text),
                "ToDecimal" => XmlConvert.ToDecimal(text),
                _ => null!,
            };
        }
        catch (System.Exception ex) when (ex is FormatException or OverflowException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, ex.GetType().Name, text));
        }
    }
}
