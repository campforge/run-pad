using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the fifth research subagent survey (full tool
// access, empirically self-verified), re-verified independently before implementing.
// `ActivityContext.Parse(string traceParent, string? traceState)` — a richer composite-format superset of
// the already-covered `ActivityTraceId.CreateFromString`/`ActivitySpanId.CreateFromString` (RPAD0416,
// проход 426), which each validate only ONE segment of the W3C traceparent header in isolation.
// `ActivityContext.Parse` validates the WHOLE `"{version}-{traceId}-{spanId}-{flags}"` composite string in
// one pass and additionally rejects cases the per-segment checks can't catch on their own: an all-zero
// trace-id or span-id that's individually well-formed hex of the right length, and a reserved version byte
// (`ff`). Confirmed empirically, matching the agent's report exactly: single exception type,
// `ArgumentException` ("Invalid trace parent."), for empty text, garbage text, wrong segment count,
// uppercase hex, all-zero trace-id, all-zero span-id, and the reserved `ff` version. Confirmed only the
// FIRST argument (`traceParent`) matters — garbage in the second argument (`traceState`) never causes a
// throw even when `traceParent` is valid, so this analyzer only inspects the first literal, same
// single-argument-relevance pattern already used by RPAD0403/0404/0405's `ImportFromEncryptedPem`
// (`input` matters, `password` doesn't).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidActivityContextTraceParentLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0420";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ActivityContext.Parse traceParent argument is not a valid W3C traceparent header",
        messageFormat: "'ActivityContext.Parse(...)' always throws ArgumentException at runtime — '{0}' is not a valid W3C traceparent header",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ActivityContext.Parse(string traceParent, string? traceState) throws ArgumentException when traceParent isn't a well-formed W3C traceparent header (wrong segment count, non-hex/wrong-length trace-id or span-id, all-zero trace-id/span-id, or a reserved version byte).");

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
        if (invocation.ArgumentList.Arguments is not [{ Expression: var traceParentArg }, _])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(traceParentArg, ct).Value is not string traceParent) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Parse",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Diagnostics.ActivityContext") return;

        try
        {
            ActivityContext.Parse(traceParent, null);
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), traceParent));
        }
    }
}
