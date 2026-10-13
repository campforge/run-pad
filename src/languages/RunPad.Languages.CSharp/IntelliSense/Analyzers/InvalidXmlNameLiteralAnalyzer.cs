using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379-RPAD0383 — grep-confirmed no prior coverage. Confirmed via
// reflection that `XmlConvert.VerifyName(string)`'s parameter carries no `[StringSyntax]` attribute (unlike
// `JsonDocument.Parse`'s, whose IDE-built-in coverage caused RPAD0257's sibling `InvalidJsonLiteralAnalyzer`
// to be dropped earlier) — no built-in-analyzer collision risk here. `XmlConvert` has SEVEN
// `VerifyXxx(string)` static methods total (confirmed via reflection); this analyzer covers only
// `VerifyName`/`VerifyNCName`, confirmed empirically to share the IDENTICAL two-exception-type profile —
// empty string throws `ArgumentException`, any other XML-Name-grammar violation (starts with a digit,
// contains whitespace, contains a disallowed character like ':' for NCName specifically) throws
// `XmlException` instead. Deliberately NOT extended to the other five siblings
// (`VerifyTOKEN`/`VerifyNMTOKEN`/`VerifyPublicId`/`VerifyXmlChars`/`VerifyWhitespace`) — confirmed
// empirically each has a DIFFERENT leniency profile (e.g. `VerifyPublicId`/`VerifyWhitespace` both accept
// an EMPTY string without throwing, unlike `VerifyName`/`VerifyNCName`), so folding them into the same
// two-exception-type dispatch without re-verifying each individually would risk a false positive — left as
// a separate, not-yet-attempted candidate rather than guessed at.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0384";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.VerifyName/VerifyNCName argument is not a valid XML name",
        messageFormat: "'XmlConvert.{0}(...)' always throws {1} at runtime — '{2}' is not a valid XML {3}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.VerifyName(string)/VerifyNCName(string) throw ArgumentException for an empty string, or XmlException for a string that isn't a well-formed XML Name/NCName.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "VerifyName" or "VerifyNCName" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string name) return;

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
            _ = methodName == "VerifyName" ? XmlConvert.VerifyName(name) : XmlConvert.VerifyNCName(name);
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or XmlException)
        {
            var kind = methodName == "VerifyName" ? "Name" : "NCName";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, ex.GetType().Name, name, kind));
        }
    }
}
