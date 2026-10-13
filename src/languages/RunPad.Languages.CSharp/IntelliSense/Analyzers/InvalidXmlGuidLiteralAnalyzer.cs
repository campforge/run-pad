using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Found by reflecting
// over `XmlConvert`'s `ToXxx(string)` static methods (the same family whose `ToBoolean`/`ToTimeSpan`/
// `ToDateTimeOffset` members are already covered — RPAD0389/0390/0391) and noticing `ToGuid(string)` had
// no coverage. Same low-novelty-but-still-worth-having situation as `InvalidSqlGuidLiteralAnalyzer`
// (RPAD0412) — confirmed empirically that `ToGuid` throws the identical single exception type
// (`FormatException`, identical message text "Unrecognized Guid format.") and accepts the identical five
// formats (D/N/B/P/X) as `Guid`'s own constructor/`Parse`, strongly suggesting `XmlConvert.ToGuid` also
// delegates to `Guid` internally — but it's still a genuinely distinct, unguarded call shape. The other
// `ToXxx` primitive-number overloads (`ToInt32`/`ToDouble`/`ToDecimal`/etc.) were NOT picked up here,
// matching the third agent's own dead-end finding for the analogous `SqlByte`/`SqlInt16`/etc. family —
// low novelty versus this catalog's existing primitive-Parse coverage, likely to just delegate to the same
// `int.Parse`/`double.Parse` this catalog's `InvalidNumericParseFormatAnalyzer` already covers.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlGuidLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0413";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.ToGuid argument is not a recognized Guid format",
        messageFormat: "'XmlConvert.ToGuid(...)' always throws FormatException at runtime — '{0}' doesn't match any of Guid's recognized formats (32 hex digits, optionally grouped 8-4-4-4-12 with dashes, optionally wrapped in braces or parentheses, or the hex-array \"X\" format)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.ToGuid(string) throws FormatException when the argument doesn't match any of Guid's recognized textual formats.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToGuid" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToGuid",
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
            XmlConvert.ToGuid(text);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), text));
        }
    }
}
