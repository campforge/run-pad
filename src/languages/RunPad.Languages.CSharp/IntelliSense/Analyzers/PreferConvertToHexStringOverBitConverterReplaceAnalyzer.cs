using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят четвёртый проход, batch) — own pick, not a Roslynator port.
// `BitConverter.ToString(bytes).Replace("-", "")` is the pre-.NET-5 idiom for hex-encoding a byte
// array — BitConverter.ToString formats as dash-separated hex pairs ("AA-BB-CC"), then Replace strips
// the dashes. `Convert.ToHexString(bytes)` (.NET 5+, well within this project's .NET 6 SDK floor)
// produces the same dash-free hex string directly, without the intermediate dashed string or the
// second pass to strip it.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferConvertToHexStringOverBitConverterReplaceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0167";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Convert.ToHexString over BitConverter.ToString().Replace(\"-\", \"\")",
        messageFormat: "'BitConverter.ToString(bytes).Replace(\"-\", \"\")' can be simplified to 'Convert.ToHexString(bytes)' — same result, without the intermediate dashed string",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Convert.ToHexString(bytes) produces the same dash-free hex string as BitConverter.ToString(bytes).Replace(\"-\", \"\") directly, without formatting dashes just to strip them again.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Replace" } replaceAccess)
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            { Expression: LiteralExpressionSyntax { Token.ValueText: "-" } },
            { Expression: LiteralExpressionSyntax { Token.ValueText: "" } },
            ])
        {
            return;
        }

        if (replaceAccess.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" },
                ArgumentList.Arguments.Count: 1,
            } toStringInvocation)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Replace" } replaceMethod)
            return;
        if (replaceMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        if (context.SemanticModel.GetSymbolInfo(toStringInvocation, ct).Symbol is not IMethodSymbol { Name: "ToString", IsStatic: true } toStringMethod)
            return;
        if (toStringMethod.ContainingType?.ToDisplayString() != "System.BitConverter") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
