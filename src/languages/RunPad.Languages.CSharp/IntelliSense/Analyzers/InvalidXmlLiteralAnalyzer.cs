using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the
// real validator" theme as InvalidGuidLiteralFormatAnalyzer/InvalidIPAddressLiteralAnalyzer (RPAD0246,
// RPAD0249): `XDocument.Parse(s)` and `XElement.Parse(s)` throw `XmlException` for a string that isn't
// well-formed XML — rather than hand-writing an XML well-formedness check, this analyzer calls the REAL
// `XDocument.Parse`/`XElement.Parse` in-process on the literal, inside a try/catch, and reports only if
// THAT call itself throws. Well-formedness is a fixed, universal grammar with no culture dependency; a
// DOCTYPE referencing an external DTD was checked empirically before writing this and does NOT attempt
// to fetch it (XDocument.Parse never resolves external entities/DTDs by default), so there's no
// network-availability or environment dependency either. Scoped to the 1-arg overloads only — the 2-arg
// `(string, LoadOptions)` overloads don't affect well-formedness, only what's preserved (whitespace,
// line info), so this is a scoping simplification rather than a correctness concern. A parallel
// `InvalidJsonLiteralAnalyzer` for `JsonDocument.Parse` was PROBEd alongside this and dropped — the IDE
// already flags malformed JSON literals passed to a `[StringSyntax("Json")]`-annotated parameter (which
// `JsonDocument.Parse`'s is) with its own built-in "JSON issue: ..." diagnostic on the same span, for the
// same underlying reason; `XDocument.Parse`/`XElement.Parse`'s parameters carry no such annotation, so
// there's no equivalent built-in coverage to collide with here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0257";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XDocument.Parse/XElement.Parse argument is not well-formed XML",
        messageFormat: "'{0}.Parse(...)' always throws XmlException at runtime — this literal is not well-formed XML",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XDocument.Parse and XElement.Parse throw XmlException for a string that isn't well-formed XML.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;

        string typeName;
        bool isValid;
        switch (method.ContainingType?.ToDisplayString())
        {
            case "System.Xml.Linq.XDocument":
                typeName = "XDocument";
                isValid = IsValidXDocument(value);
                break;
            case "System.Xml.Linq.XElement":
                typeName = "XElement";
                isValid = IsValidXElement(value);
                break;
            default:
                return;
        }
        if (isValid) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), typeName));
    }

    private static bool IsValidXDocument(string value)
    {
        try
        {
            _ = XDocument.Parse(value);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsValidXElement(string value)
    {
        try
        {
            _ = XElement.Parse(value);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
