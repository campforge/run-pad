using System.Buffers.Text;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.Text.Base64Url.DecodeFromChars(ReadOnlySpan<char>)` (.NET 9+, new to this catalog —
// first analyzer touching the `System.Buffers.Text` namespace). Confirmed empirically: the single-argument
// overload (the one returning `byte[]`) throws `FormatException` for text containing a character outside
// the URL-safe Base64 alphabet (standard Base64's '+'/'/' are themselves rejected — the whole point of the
// URL-safe variant), or a length that isn't valid Base64 padding-wise (e.g. 2 or 3 characters with no
// trailing '='). The empty string does NOT throw (matches Convert.FromBase64String's established leniency
// for empty input) — confirmed, matching the agent's report. Also confirmed (a genuine surprise, not in the
// agent's report): whitespace characters ARE silently skipped/ignored by the decoder rather than rejected,
// same as `Convert.FromBase64String` — so a literal with embedded spaces/tabs/newlines around otherwise
// valid data is NOT flagged; this needs no special handling in the analyzer since it simply runs the real
// decoder on the literal rather than hand-rolling a length/alphabet check. Scoped strictly to the 1-argument
// overload (`ArgumentList.Arguments` count == 1) to avoid matching the sibling
// `DecodeFromChars(ReadOnlySpan<char>, Span<byte>)` (returns bytes-written count, different semantics) or
// the 5-argument `TryDecodeFromChars`-shaped overload.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBase64UrlLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0406";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Base64Url.DecodeFromChars argument is not a valid URL-safe Base64 string",
        messageFormat: "'Base64Url.DecodeFromChars(...)' always throws FormatException at runtime — '{0}' is not a valid URL-safe Base64 string (standard Base64's '+'/'/' characters are not accepted)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Base64Url.DecodeFromChars(ReadOnlySpan<char>) throws FormatException for text containing a character outside the URL-safe Base64 alphabet, or an invalid length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "DecodeFromChars" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "DecodeFromChars",
                IsStatic: true,
                Parameters: [{ Type: { } paramType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (paramType.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Buffers.Text.Base64Url") return;

        try
        {
            Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), text));
        }
    }
}
