using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (четыреста седьмой проход, batch) — own pick, not a Roslynator port; found by direct
// brainstorming after the second research subagent's list (проход 397) was fully exhausted. The agent's
// own report had flagged `XComment`/`XProcessingInstruction` as "uncertain — LINQ-to-XML is known to defer
// some validation to serialization time, not construction — could be a dead end like new Oid(string)".
// Checked both empirically: `new XComment(string)` IS a confirmed dead end — it never throws for ANY input,
// including content containing "--" (illegal inside an XML comment) or ending in "-" (also illegal) —
// LINQ-to-XML silently SANITIZES the content at construction time (inserting a space to break up "--" or
// append after a trailing "-") instead of throwing, the same "lenient, never throws" shape as `new
// Oid(string)` from проход 395. `new XProcessingInstruction(string target, string data)`, however, is NOT a
// dead end — confirmed empirically it genuinely throws for a bad TARGET (though, matching XComment's
// leniency, a `data` value containing the PI-closing "?>" sequence is similarly sanitized rather than
// rejected, confirmed empirically before assuming `data` might also throw). Confirmed empirically this is a
// TWO-exception-type situation: a target name reserved by the XML spec (any casing of "xml" as a prefix)
// throws `ArgumentException`, while a target that isn't a syntactically valid XML Name at all (e.g.
// starting with a digit) throws `System.Xml.XmlException` instead.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXProcessingInstructionLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0399";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XProcessingInstruction target argument is not valid",
        messageFormat: "'new XProcessingInstruction(...)' always throws {0} at runtime — '{1}' is not a valid processing instruction target",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The XProcessingInstruction(string, string) constructor throws ArgumentException for a target reserved by the XML spec (any casing of \"xml\"), or XmlException for a target that isn't a valid XML Name.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ct = context.CancellationToken;
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var targetArg }, _] }) return;
        if (context.SemanticModel.GetConstantValue(targetArg, ct).Value is not string target) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_String }],
            } method)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Xml.Linq.XProcessingInstruction") return;

        try
        {
            _ = new XProcessingInstruction(target, "data");
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.Xml.XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), ex.GetType().Name, target));
        }
    }
}
