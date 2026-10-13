using System.Collections.Immutable;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; continued direct brainstorming over
// the `XmlConvert.ToXxx` family after `ToGuid` (RPAD0413). `ToDateTime` is the plain (non-
// offset) sibling of `ToDateTimeOffset` (RPAD0391) — confirmed via reflection FOUR overloads
// exist: a bare `ToDateTime(string)`, `(string, string)` and `(string, string[])` format-specifier
// variants, and `(string, XmlDateTimeSerializationMode)`. The bare single-string overload is marked
// `[Obsolete]` (confirmed via reflection, not assumed) — calling it directly would trigger a CS0618
// warning under this project's `-warnaserror`, so this analyzer scopes to the non-obsolete
// `(string, XmlDateTimeSerializationMode)` overload instead, checking only the string argument and
// ignoring the enum. Confirmed empirically, matching `ToDateTimeOffset`'s already-established profile:
// single exception type, `FormatException` ("The string '...' is not a valid AllXsd value."), for empty
// text, garbage text, out-of-range components (month 13, hour 25), and non-ISO-8601 formats like US-style
// "01/01/2024" that plain `DateTime.Parse` would accept but this strict XSD-grammar method rejects.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidXmlDateTimeLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0414";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "XmlConvert.ToDateTime argument is not a valid XSD dateTime literal",
        messageFormat: "'XmlConvert.ToDateTime(...)' always throws FormatException at runtime — '{0}' is not a valid XSD dateTime literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "XmlConvert.ToDateTime(string, XmlDateTimeSerializationMode) throws FormatException for a string that isn't a valid ISO 8601/XSD dateTime — it has no culture-based fallback, unlike DateTime.Parse.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToDateTime" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }, _]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ToDateTime",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type: { } modeType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (modeType.ToDisplayString() != "System.Xml.XmlDateTimeSerializationMode") return;
        if (containingType.ToDisplayString() != "System.Xml.XmlConvert") return;

        try
        {
            _ = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.RoundtripKind);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
        }
    }
}
