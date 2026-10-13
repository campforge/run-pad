using System.Collections.Immutable;
using System.Runtime.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the fourth research subagent survey (424-й
// проход, full tool access, empirically self-verified), re-verified independently before implementing.
// `TimeZoneInfo.FromSerializedString(string)` — NOT covered by `InvalidTimeZoneIdLiteralAnalyzer`
// (RPAD0380, проход 388), which only targets the DIFFERENT method `FindSystemTimeZoneById` (a single-
// overload/single-method-name match, confirmed by re-reading that file). Confirmed empirically, matching
// the agent's report exactly: TWO exception types depending on input SHAPE — an empty string throws
// `ArgumentException`, while any other non-empty-but-corrupt text (garbage, truncated real serialized
// data, whitespace-only) throws `System.Runtime.Serialization.SerializationException` instead. A real
// round-tripped serialized string (`TimeZoneInfo.Utc.ToSerializedString()`) is accepted without throwing.
// The agent flagged this candidate's real-world usefulness as low — the `;`-delimited serialized format is
// meant to be round-tripped via `ToSerializedString()`, not hand-authored as a literal — but it's still a
// genuinely distinct, verified, non-duplicate always-throws pattern, consistent with this catalog's
// existing precedent of still implementing lower-novelty-but-clean candidates (SqlGuid/XmlGuid, RPAD0412/
// RPAD0413, проходы 421-422).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTimeZoneInfoSerializedStringLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0417";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TimeZoneInfo.FromSerializedString argument is not a valid serialized TimeZoneInfo",
        messageFormat: "'TimeZoneInfo.FromSerializedString(...)' always throws {0} at runtime — the text is not a valid TimeZoneInfo serialized string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TimeZoneInfo.FromSerializedString(string) throws ArgumentException for an empty string, or SerializationException for other corrupt/malformed serialized data.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FromSerializedString" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "FromSerializedString",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.TimeZoneInfo") return;

        try
        {
            TimeZoneInfo.FromSerializedString(text);
        }
        catch (System.Exception ex) when (ex is ArgumentException or SerializationException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name));
        }
    }
}
