using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят шестой проход, batch) — own pick, not a Roslynator port.
// `xs.Count == 0 ? default : xs[0]` (or `xs.Length == 0 ? default : xs[0]` for an array) hand-writes
// exactly what `xs.FirstOrDefault()` already does: return the first element, or default(T) when the
// collection is empty. Matched structurally (the Count/Length receiver and the indexer receiver must
// be the literal same expression — SyntaxNode.IsEquivalentTo) and requires the index to be the literal
// `0` and the "empty" branch to be `default`/`default(T)`/`null`.
// Extended (двести тридцать второй проход, batch) to also cover the opposite polarity —
// `xs.Count > 0 ? xs[0] : default` says the same thing the other way around.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferFirstOrDefaultOverCountCheckTernaryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0171";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer FirstOrDefault over a Count/Length empty-check ternary",
        messageFormat: "'{0}' can be simplified to 'xs.FirstOrDefault()'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A ternary that returns default when Count/Length is zero and otherwise the first element hand-writes exactly what FirstOrDefault() already does.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ConditionalExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var conditional = (ConditionalExpressionSyntax)context.Node;
        if (conditional.Condition is not BinaryExpressionSyntax
            {
                Left: MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" or "Length" } countAccess,
                Right: LiteralExpressionSyntax { Token.Value: 0 },
            } condition)
        {
            return;
        }

        ExpressionSyntax defaultBranch;
        ExpressionSyntax elementBranch;
        string illustrativeForm;
        if (condition.IsKind(SyntaxKind.EqualsExpression))
        {
            defaultBranch = conditional.WhenTrue;
            elementBranch = conditional.WhenFalse;
            illustrativeForm = "xs.Count == 0 ? default : xs[0]";
        }
        else if (condition.IsKind(SyntaxKind.GreaterThanExpression))
        {
            defaultBranch = conditional.WhenFalse;
            elementBranch = conditional.WhenTrue;
            illustrativeForm = "xs.Count > 0 ? xs[0] : default";
        }
        else
        {
            return;
        }

        if (!IsDefaultLike(defaultBranch)) return;

        if (elementBranch is not ElementAccessExpressionSyntax
            {
                ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax { Token.Value: 0 } }],
            } elementAccess)
        {
            return;
        }
        if (!countAccess.Expression.IsEquivalentTo(elementAccess.Expression)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.GetLocation(), illustrativeForm));
    }

    private static bool IsDefaultLike(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression or (int)SyntaxKind.NullLiteralExpression } => true,
        DefaultExpressionSyntax => true,
        _ => false,
    };
}
