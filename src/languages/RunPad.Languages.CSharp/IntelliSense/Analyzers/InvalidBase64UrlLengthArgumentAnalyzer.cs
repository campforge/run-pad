using System.Buffers.Text;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.Text.Base64Url.
// GetMaxDecodedLength(int base64Length)`/`.GetEncodedLength(int bytesLength)` — bundled under one
// diagnostic (precedent RPAD0426); distinct from the already-covered `InvalidBase64UrlLiteralAnalyzer`
// (RPAD0406, which is scoped to the 1-arg `DecodeFromChars(ReadOnlySpan<char>)` content-validity check
// — confirmed via reading its doc comment before implementing, per the standing duplicate-check
// discipline). `GetMaxDecodedLength`: negative `base64Length` throws `ArgumentOutOfRangeException`.
// `GetEncodedLength`: throws `ArgumentOutOfRangeException` when the value, reinterpreted as `uint`,
// exceeds `1610612733` — confirmed exact boundary (1610612733 valid, 1610612734 throws) and confirmed
// any negative `int` literal always throws too (casts to a huge `uint`, always past the threshold).
// Both static, both pure length-arithmetic (no side effects), no `[SupportedOSPlatform]` gating.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBase64UrlLengthArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0490";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Base64Url length argument is out of range",
        messageFormat: "'Base64Url.{0}({1})' always throws ArgumentOutOfRangeException at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Base64Url.GetMaxDecodedLength throws for a negative argument; Base64Url.GetEncodedLength throws when the argument (reinterpreted as uint) exceeds 1610612733.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        var isMaxDecoded = methodName == "GetMaxDecodedLength";
        if (!isMaxDecoded && methodName != "GetEncodedLength") return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Buffers.Text.Base64Url") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lengthArg }]) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;

        try
        {
            if (isMaxDecoded) _ = Base64Url.GetMaxDecodedLength(length);
            else _ = Base64Url.GetEncodedLength(length);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            var reason = isMaxDecoded ? "base64Length must be non-negative" : "bytesLength must not exceed 1610612733";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, length, reason));
        }
    }
}
