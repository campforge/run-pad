using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as InvalidBase64LiteralLengthAnalyzer (RPAD0245, проход 252):
// `Guid.Parse(s)` throws `FormatException` unless `s` (after trimming leading/trailing whitespace,
// which Guid.Parse itself ignores) matches one of the handful of formats Guid recognizes — 32 hex
// digits with no separators ("N"), the same 32 digits grouped 8-4-4-4-12 with dashes ("D"), or that "D"
// form wrapped in braces ("B") or parentheses ("P"). Deliberately does NOT attempt to recognize the
// fifth format ("X", the `{0x########, ..., {0x##, ...}}` array-literal shape) — it's rare enough in
// real code, and complex enough to validate correctly, that misjudging it would risk a false positive;
// a literal that doesn't match any of the four common formats is still flagged, on the assumption that
// nobody hand-writes the X format by accident.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidGuidLiteralFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0246";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Guid.Parse argument does not match a recognized Guid format",
        messageFormat: "'Guid.Parse(...)' always throws FormatException at runtime — this literal doesn't match any of Guid's recognized formats (32 hex digits, optionally grouped 8-4-4-4-12 with dashes, optionally wrapped in braces or parentheses)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Guid.Parse throws FormatException unless the input matches one of Guid's recognized formats.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly Regex ValidGuidPattern = new(
        @"^(\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}|\([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\)|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32})$",
        RegexOptions.Compiled);

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
        if (ValidGuidPattern.IsMatch(value.Trim())) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Guid") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
