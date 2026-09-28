using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (426-й проход, batch) — candidate #2 from the fourth research subagent survey (424-й
// проход, full tool access, empirically self-verified), re-verified independently before implementing.
// `System.Diagnostics.ActivityTraceId.CreateFromString(ReadOnlySpan<char>)` and
// `ActivitySpanId.CreateFromString(ReadOnlySpan<char>)` — W3C-trace-context distributed-tracing IDs, new
// to this catalog (first analyzer touching `System.Diagnostics`'s Activity API). Confirmed empirically,
// matching the agent's report exactly: single exception type, `ArgumentOutOfRangeException`, for anything
// that isn't EXACTLY 32 (TraceId) / 16 (SpanId) lowercase hex characters — confirmed uppercase hex of the
// correct length is REJECTED (must be lowercase specifically), and confirmed the all-zeros ID (the
// reserved "invalid ID" value per the W3C spec) is rejected too despite being hex-valid-length. Both
// entry points share the identical method name/shape (`static CreateFromString(ReadOnlySpan<char>)`), so
// one analyzer dispatches on `ContainingType` the same way `InvalidCultureNameLiteralAnalyzer`/
// `InvalidGuidConstructorLiteralAnalyzer` dispatch across multiple call shapes. Real-world hit rate is
// niche (hardcoded trace/span IDs mostly appear in test code), but the check is clean and unconditional.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidActivityIdLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0416";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ActivityTraceId/ActivitySpanId.CreateFromString argument is not a valid W3C trace-context ID",
        messageFormat: "'{0}.CreateFromString(...)' always throws ArgumentOutOfRangeException at runtime — '{1}' is not {2} lowercase hex characters, or is the reserved all-zeros ID",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ActivityTraceId.CreateFromString/ActivitySpanId.CreateFromString throw ArgumentOutOfRangeException for text that isn't exactly 32/16 lowercase hex characters, or for the reserved all-zeros ID.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateFromString" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "CreateFromString",
                IsStatic: true,
                Parameters: [{ Type: { } paramType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (paramType.ToDisplayString() != "System.ReadOnlySpan<char>") return;

        string typeName;
        string lengthDescription;
        bool isValid;
        switch (containingType.ToDisplayString())
        {
            case "System.Diagnostics.ActivityTraceId":
                typeName = "ActivityTraceId";
                lengthDescription = "32";
                isValid = TryCreate(() => ActivityTraceId.CreateFromString(text));
                break;
            case "System.Diagnostics.ActivitySpanId":
                typeName = "ActivitySpanId";
                lengthDescription = "16";
                isValid = TryCreate(() => ActivitySpanId.CreateFromString(text));
                break;
            default:
                return;
        }
        if (isValid) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), typeName, text, lengthDescription));
    }

    private static bool TryCreate(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
