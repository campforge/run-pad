using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. `Guid.ParseExact(input,
// format)` throws FormatException for ANY `format` value outside the fixed set `{"D","d","N","n","P","p",
// "B","b","X","x"}` — REGARDLESS of what `input` contains, confirmed empirically via scratch `dotnet run`
// script (not assumed) by reading the exact set out of the BCL's own exception message text ("Format
// string can be only \"D\", \"d\", \"N\", \"n\", \"P\", \"p\", \"B\", \"b\", \"X\" or \"x\"."). Unlike most
// other analyzers in this catalog, this does NOT call the real validator with a placeholder input — doing
// so safely would require generating a correctly-shaped placeholder GUID string per format (dashes for
// "D", braces for "B", parentheses for "P", a hex-int-array literal for "X", etc.), which is more complex
// and error-prone than just checking membership in the small, stable, documented set directly. Scoped to a
// literal `format` string only.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidGuidParseExactFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0289";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Guid.ParseExact format specifier always throws",
        messageFormat: "'Guid.ParseExact(..., \"{0}\")' always throws FormatException at runtime — format must be one of \"D\", \"d\", \"N\", \"n\", \"P\", \"p\", \"B\", \"b\", \"X\", or \"x\"",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Guid.ParseExact(input, format) throws FormatException for any format string outside the fixed set \"D\"/\"d\"/\"N\"/\"n\"/\"P\"/\"p\"/\"B\"/\"b\"/\"X\"/\"x\", regardless of the input string's content.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> ValidFormats = ImmutableHashSet.Create(
        "D", "d", "N", "n", "P", "p", "B", "b", "X", "x");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ParseExact" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var formatArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(formatArg, ct).Value is not string format) return;
        if (ValidFormats.Contains(format)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ParseExact" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Guid") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), format));
    }
}
