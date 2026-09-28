using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (416-й проход, batch) — candidate #4 from the third research subagent survey (411-й проход,
// full tool access, empirically self-verified), re-verified independently before implementing.
// `new XDocumentType(string name, string? publicId, string? systemId, string? internalSubset)` — scoped
// to ONLY the `name` argument, per the agent's recommendation, matching what's empirically confirmed:
// garbage `publicId`/`systemId`/`internalSubset` literals never throw (they're stored as opaque data, not
// validated), while `name` is checked against XML Name production rules. Confirmed a THIRD exception type
// beyond the agent's two-exception-type report: an empty string throws `ArgumentException`, a literal
// `null` throws `ArgumentNullException` (not reachable through this analyzer, since `GetConstantValue` on a
// `null` literal yields a constant whose `Value` is `null`, not a `string` — the `is string` pattern match
// already filters it out, same as every other analyzer in this catalog), and text that violates XML's Name
// production (e.g. starting with a digit, containing a space) throws `System.Xml.XmlException`. Same
// technique as InvalidXmlNameLiteralAnalyzer (RPAD0384, проход 392), but calling the REAL
// `XDocumentType` constructor directly instead of `XmlConvert.VerifyName`, since the exact validation this
// constructor performs on `name` is what matters here, not just XML-Name-in-general.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXDocumentTypeNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0407";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XDocumentType name argument is not a valid XML Name",
        messageFormat: "'new XDocumentType(...)' always throws {0} at runtime — '{1}' is not a valid XML Name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XDocumentType(string name, ...) throws ArgumentException for an empty name, or XmlException for text that violates XML's Name production rules.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        if (creation.ArgumentList is not { Arguments: [{ Expression: var nameArg }, ..] }) return;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string name) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.Linq.XDocumentType") return;

        try
        {
            _ = new XDocumentType(name, null, null, null);
        }
        catch (System.Exception ex) when (ex is ArgumentException or System.Xml.XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), ex.GetType().Name, name));
        }
    }
}
