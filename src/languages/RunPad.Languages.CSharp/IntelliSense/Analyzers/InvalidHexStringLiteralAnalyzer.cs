using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести шестьдесят второй проход, batch) — own pick, not a Roslynator port. Same "run the
// real validator" theme as the rewritten InvalidBase64LiteralLengthAnalyzer (RPAD0245, this same
// проход): `Convert.FromHexString(s)` throws `FormatException` unless `s` has an even number of
// characters, all of them hex digits — rather than hand-writing that check, this analyzer calls the
// REAL non-throwing `Convert.FromHexString(string, Span<byte>, out int, out int)` overload (which
// returns an `OperationStatus` instead of throwing) in-process on the literal, into a scratch buffer
// sized to the literal's own length (always large enough, since decoded output is at most half the
// encoded input's length), and reports only if the status isn't `OperationStatus.Done` — empirically
// confirmed to agree with the throwing overload's throw/no-throw outcome, including that (unlike Base64)
// embedded whitespace makes a hex string invalid, before writing this. Hex-string syntax has no
// external, environment-dependent data table behind it (it's a pure string algorithm), so there's no
// risk of the analysis machine and the deployment target disagreeing on what's valid.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHexStringLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0256";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.FromHexString argument is not a valid hex string",
        messageFormat: "'Convert.FromHexString(...)' always throws FormatException at runtime — '{0}' is not a valid hex string (must have an even number of hex digits and no other characters)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.FromHexString throws FormatException for a string with an odd number of characters or containing non-hex-digit characters.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FromHexString" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "FromHexString" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;

        var status = Convert.FromHexString(value, new byte[value.Length], out _, out _);
        if (status == OperationStatus.Done) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }
}
