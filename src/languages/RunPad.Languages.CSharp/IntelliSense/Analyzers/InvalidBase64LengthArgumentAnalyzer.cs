using System.Buffers.Text;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.Text.Base64.
// GetMaxDecodedFromUtf8Length(int)`/`.GetMaxEncodedToUtf8Length(int)` — bundled under one diagnostic
// (precedent RPAD0426). Same exact boundary shape as the already-covered `Base64Url.
// GetMaxDecodedLength`/`.GetEncodedLength` (RPAD0490), but a genuinely distinct sibling type with
// differently-named methods — same "cover the sibling class separately" precedent already used
// elsewhere in this catalog (e.g. `Rune(char)` vs `Rune(int)`). Confirmed empirically:
// `GetMaxDecodedFromUtf8Length` throws `ArgumentOutOfRangeException` for a negative `length`;
// `GetMaxEncodedToUtf8Length` throws `ArgumentOutOfRangeException` when the value, reinterpreted as
// `uint`, exceeds `1610612733` (exact boundary confirmed: 1610612733 valid, 1610612734 throws; any
// negative literal also always throws, casting to a huge `uint`). Both static, both pure
// length-arithmetic (no side effects), no `[SupportedOSPlatform]` gating. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBase64LengthArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0493";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Base64 length argument is out of range",
        messageFormat: "'Base64.{0}({1})' always throws ArgumentOutOfRangeException at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Base64.GetMaxDecodedFromUtf8Length throws for a negative argument; Base64.GetMaxEncodedToUtf8Length throws when the argument (reinterpreted as uint) exceeds 1610612733.");

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
        var isDecoded = methodName == "GetMaxDecodedFromUtf8Length";
        if (!isDecoded && methodName != "GetMaxEncodedToUtf8Length") return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Buffers.Text.Base64") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lengthArg }]) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;

        try
        {
            if (isDecoded) _ = Base64.GetMaxDecodedFromUtf8Length(length);
            else _ = Base64.GetMaxEncodedToUtf8Length(length);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            var reason = isDecoded ? "length must be non-negative" : "length must not exceed 1610612733";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, length, reason));
        }
    }
}
