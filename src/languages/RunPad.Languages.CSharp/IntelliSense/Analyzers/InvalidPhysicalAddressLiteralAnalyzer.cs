using System.Collections.Immutable;
using System.Net.NetworkInformation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста семьдесят пятый проход, batch) — own pick, not a Roslynator port. Same "run the
// real validator" theme as InvalidIPAddressLiteralAnalyzer/InvalidIPNetworkLiteralAnalyzer/
// InvalidIPEndPointLiteralAnalyzer (RPAD0249/RPAD0366/RPAD0367) — `System.Net.NetworkInformation.
// PhysicalAddress.Parse(string)` (a MAC address, e.g. "00-14-22-01-23-45") throws `FormatException` for
// a string that isn't a valid physical address. No structural pre-filter guard needed before calling the
// real `TryParse` (unlike the numeric/date-time Parse analyzers, RPAD0356/RPAD0365) — MAC address syntax
// is a pure ASCII-hex-digit algorithm with no culture-dependent interpretation, the same reasoning
// RPAD0249/RPAD0366/RPAD0367 already rely on for IP address/network/endpoint syntax. Confirmed
// empirically (scratch `dotnet run` script) several easy-to-miss details that would have made a
// hand-rolled regex/length check wrong, reinforcing why this project always reaches for the real
// validator instead: an EMPTY string does NOT throw (returns a zero-length address); `PhysicalAddress`
// has no fixed 6-byte requirement — any EVEN number of hex digits is accepted (`"00-14-22"`, 3 bytes,
// parses fine); both `-` and `:` work as separators, but only in the canonical byte-pair-grouped
// position — a leading, trailing, every-other-character, or mixed-separator placement (`"--001422"`,
// `"0-0-1-4-2-2"`, `"001422-"`, `"00-14:22"`) all throw despite every individual character being
// otherwise valid; an ODD number of hex digits, or any non-hex/non-separator character, always throws.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPhysicalAddressLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0368";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "PhysicalAddress.Parse argument is not a valid MAC address",
        messageFormat: "'PhysicalAddress.Parse(...)' always throws FormatException at runtime — '{0}' is not a valid physical (MAC) address",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PhysicalAddress.Parse throws FormatException for a string that isn't a valid physical (MAC) address.");

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
        if (method.ContainingType?.ToDisplayString() != "System.Net.NetworkInformation.PhysicalAddress") return;

        if (PhysicalAddress.TryParse(value, out _)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }
}
