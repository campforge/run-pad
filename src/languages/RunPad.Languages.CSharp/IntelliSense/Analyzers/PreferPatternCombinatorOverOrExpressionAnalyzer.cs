using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `x is A || x is B` re-checks and re-reads `x` twice to ask "is x one of these types" — the pattern
// combinator `x is A or B` (C# 9+, always available given this project's own
// MinSupportedSdkMajorVersion=6) says the same thing in one check, without repeating `x`. Matched
// structurally (both sides must be a bare `is` check against the literal same expression —
// SyntaxNode.IsEquivalentTo). Deliberately only the two-operand shape — a 3+-way chain
// (`x is A || x is B || x is C`) still gets the innermost pair flagged, since the outer `||` node's
// left side is itself a `||` expression, not a bare `is` check, and guessing how to merge three-plus
// patterns isn't worth the risk of getting it wrong.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferPatternCombinatorOverOrExpressionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0115";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the or pattern combinator over separate is checks",
        messageFormat: "'x is A || x is B' can be combined into a single pattern — 'x is A or B'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "x is A || x is B repeats x and re-checks it twice; the or pattern combinator (x is A or B) says the same thing in one check.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalOrExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (!TryGetIsCheckReceiver(binary.Left, out var leftReceiver)) return;
        if (!TryGetIsCheckReceiver(binary.Right, out var rightReceiver)) return;
        if (!leftReceiver.IsEquivalentTo(rightReceiver)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool TryGetIsCheckReceiver(ExpressionSyntax expression, out ExpressionSyntax receiver)
    {
        if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression, Left: var left, Right: TypeSyntax })
        {
            receiver = left;
            return true;
        }
        receiver = expression;
        return false;
    }
}
