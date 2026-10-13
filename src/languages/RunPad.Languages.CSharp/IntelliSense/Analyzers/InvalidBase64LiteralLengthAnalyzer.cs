using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A correctness
// finding: `Convert.FromBase64String` throws `FormatException` for a string that isn't valid Base64 —
// originally this only checked the length-multiple-of-4 invariant by hand (skipping any literal with
// whitespace, since Convert.FromBase64String strips it before validating and a raw character count
// wouldn't be reliable). Rewritten (двести шестьдесят второй проход, batch) to the "run the real
// validator" technique established by InvalidGuidLiteralFormatAnalyzer/InvalidIPAddressLiteralAnalyzer
// (RPAD0246, RPAD0249): calls the REAL `Convert.TryFromBase64String` in-process on the literal (into a
// scratch buffer sized to the literal's own length, always large enough since decoded output can never
// exceed the encoded input's length) and reports only if THAT call itself returns false — catches every
// way a literal can be invalid (wrong length, invalid characters, misplaced padding), not just the
// length special case, and correctly handles embedded/surrounding whitespace for free, empirically
// confirmed to agree with FromBase64String's throw/no-throw outcome before rewriting.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBase64LiteralLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0245";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Base64 string literal is not valid Base64",
        messageFormat: "'Convert.FromBase64String(...)' always throws FormatException at runtime — '{0}' is not a valid Base64 string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.FromBase64String throws FormatException for a string that isn't valid Base64 (wrong length, invalid characters, or misplaced padding).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FromBase64String" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "FromBase64String" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;

        if (Convert.TryFromBase64String(value, new byte[value.Length], out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }
}
