using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Discovered RPAD0386 registers ONLY on `SyntaxKind.InvocationExpression` matching
// `XName.Get(...)` — it does NOT match `new XElement(string, ...)`/`new XAttribute(string, ...)`, where
// the string argument implicitly converts to `XName` via `XName`'s own implicit conversion operator (a
// genuinely different syntax shape, `ObjectCreationExpression`, not an invocation of `Get`). Confirmed
// empirically this implicit-conversion path throws IDENTICALLY to calling `XName.Get` directly on the
// same literal (same exception types, same messages) — `XElement` has three `XName`-first constructors
// (bare, with a single content object, with a `params object[]` content array), `XAttribute` has one
// (`XName`, `object value`); all four confirmed to validate the name argument the same way. Reuses
// `XName.Get` itself as the canonical "run the real validator" probe rather than duplicating RPAD0386's
// two-exception-type dispatch logic.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXElementOrXAttributeNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0428";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XElement/XAttribute name argument is not a valid expanded XML name",
        messageFormat: "'new {0}(...)' always throws {1} at runtime — '{2}' is not a valid expanded XML name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new XElement(name, ...)/new XAttribute(name, value) throw ArgumentException for an empty string or unbalanced \"{namespace}\" braces, or XmlException for a string that also fails plain XML Name validation — the same validation XName.Get(string) performs, since the string argument implicitly converts to XName.");

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
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string expandedName) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type: { } nameParamType }, ..],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (nameParamType.ToDisplayString() != "System.Xml.Linq.XName") return;

        var typeName = containingType.ToDisplayString() switch
        {
            "System.Xml.Linq.XElement" => "XElement",
            "System.Xml.Linq.XAttribute" => "XAttribute",
            _ => null,
        };
        if (typeName is null) return;

        try
        {
            _ = XName.Get(expandedName);
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.Xml.XmlException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), typeName, ex.GetType().Name, expandedName));
        }
    }
}
