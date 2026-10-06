using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста девяносто четвёртый проход, batch) — own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379-RPAD0385 — grep-confirmed no prior coverage, reflection-
// confirmed the parameter carries no `[StringSyntax]` attribute (no built-in-analyzer collision risk, same
// check already applied before RPAD0384). Same "run the real validator" theme, applied to the static
// factory `System.Xml.Linq.XName.Get(string expandedName)`, which parses Clark notation
// ("{namespace}localname" or a bare "localname"). Confirmed empirically this is genuinely a
// TWO-exception-type situation: an empty string, an unterminated "{...", or an empty-namespace "{}..."
// throw `ArgumentException`, while a string that fails the "{...}" parse AND THEN also fails plain XML Name
// validation (e.g. a stray leading "}" with no matching open brace) throws `System.Xml.XmlException`
// instead — the message format parameterizes the exception type name, same technique this catalog's other
// multi-exception-type analyzers already use. Also confirmed the parser is lenient in a way worth noting
// for correct test literals: a namespace value containing its own literal "{"/"}" characters (e.g.
// "{outer{inner}}bad") is accepted without throwing — only the OUTERMOST brace pair is significant, nested
// braces are just part of the namespace text.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0386";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XName.Get argument is not a valid expanded XML name",
        messageFormat: "'XName.Get(...)' always throws {0} at runtime — '{1}' is not a valid expanded XML name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XName.Get(string) throws ArgumentException for an empty string or unbalanced \"{namespace}\" braces, or XmlException for a string that also fails plain XML Name validation.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Get" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string expandedName) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Get",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Xml.Linq.XName") return;

        try
        {
            _ = XName.Get(expandedName);
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.Xml.XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, expandedName));
        }
    }
}
