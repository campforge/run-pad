using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `flags.HasFlag(A) && flags.HasFlag(B)` checks two separate flags one at a time — `HasFlag(x)`
// already checks that ALL bits of `x` are set, so `flags.HasFlag(A | B)` asks the exact same question
// (every bit of A set AND every bit of B set) in a single call.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferCombinedHasFlagAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0211";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer a single combined HasFlag call",
        messageFormat: "'flags.HasFlag(A) && flags.HasFlag(B)' can be replaced with 'flags.HasFlag(A | B)' — HasFlag already checks that every bit of its argument is set",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "HasFlag(A) && HasFlag(B) on the same value asks whether every bit of A is set and every bit of B is set — HasFlag(A | B) asks the same question in one call.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalAndExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (!TryGetHasFlagCall(binary.Left, out var leftReceiver)) return;
        if (!TryGetHasFlagCall(binary.Right, out var rightReceiver)) return;
        if (!leftReceiver.IsEquivalentTo(rightReceiver)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(binary.Left, ct).Symbol is not IMethodSymbol { Name: "HasFlag" } leftMethod)
            return;
        if (leftMethod.ContainingType?.ToDisplayString() != "System.Enum") return;

        if (context.SemanticModel.GetSymbolInfo(binary.Right, ct).Symbol is not IMethodSymbol { Name: "HasFlag" } rightMethod)
            return;
        if (rightMethod.ContainingType?.ToDisplayString() != "System.Enum") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool TryGetHasFlagCall(ExpressionSyntax expression, out ExpressionSyntax receiver)
    {
        receiver = null!;
        if (expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "HasFlag", Expression: var candidateReceiver },
                ArgumentList.Arguments.Count: 1,
            })
        {
            return false;
        }
        receiver = candidateReceiver;
        return true;
    }
}
