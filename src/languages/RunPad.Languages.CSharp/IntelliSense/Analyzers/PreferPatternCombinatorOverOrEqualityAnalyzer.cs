using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят девятый проход, batch) — own pick, not a Roslynator port. Sibling to
// PreferPatternCombinatorOverOrExpressionAnalyzer (RPAD0115, проход 170), which covers `x is A || x is
// B` — that one only matches `is` type-checks, not equality (verified by reading its Analyze method
// before writing this one). `x == 1 || x == 2` re-checks and re-reads `x` twice to ask "is x one of
// these values" — the `or` pattern combinator (`x is 1 or 2`, C# 9+) says the same thing in one check.
// Scoped to constant right-hand sides only (checked via GetConstantValue) — a constant pattern is the
// only kind `is` can express this way; comparing against a variable isn't a valid pattern at all.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferPatternCombinatorOverOrEqualityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0153";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the or pattern combinator over separate equality checks",
        messageFormat: "'x == a || x == b' can be combined into a single pattern — 'x is a or b'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "x == a || x == b repeats x and re-checks it twice; the or pattern combinator (x is a or b) says the same thing in one check for constant comparisons.");

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
        if (!TryGetEqualityReceiver(context, binary.Left, out var leftReceiver)) return;
        if (!TryGetEqualityReceiver(context, binary.Right, out var rightReceiver)) return;
        if (!leftReceiver!.IsEquivalentTo(rightReceiver)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool TryGetEqualityReceiver(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, out ExpressionSyntax? receiver)
    {
        receiver = null;
        if (expression is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } equality) return false;

        var ct = context.CancellationToken;
        var (candidateReceiver, candidateConstant) = IsConstant(context, equality.Right, ct)
            ? (equality.Left, equality.Right)
            : IsConstant(context, equality.Left, ct)
                ? (equality.Right, equality.Left)
                : (null, null);
        if (candidateReceiver is null || candidateConstant is null) return false;

        receiver = candidateReceiver;
        return true;
    }

    private static bool IsConstant(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, CancellationToken ct) =>
        context.SemanticModel.GetConstantValue(expression, ct).HasValue;
}
