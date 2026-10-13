using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "ternary hand-writes what a dedicated method already does" theme as
// PreferFirstOrDefaultOverCountCheckTernaryAnalyzer (RPAD0171), for a different shape:
// `xs.Any() ? xs.First() : default` (or `xs.Any(pred) ? xs.First(pred) : default`, or the Last/
// LastOrDefault mirror) checks for existence and then re-walks the sequence to fetch the element —
// `xs.FirstOrDefault()`/`xs.FirstOrDefault(pred)`/`xs.LastOrDefault()`/`xs.LastOrDefault(pred)` do both
// in a single pass, without ever needing an existence check at all. Matched structurally (the receiver
// of Any and the receiver of First/Last must be the literal same expression, and any predicate argument
// must also match structurally between the two calls) plus a semantic check that both resolve to the
// LINQ Enumerable methods.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferOrDefaultOverAnyCheckTernaryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0222";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer FirstOrDefault/LastOrDefault over an Any() existence-check ternary",
        messageFormat: "'xs.Any() ? xs.{0}() : default' can be simplified to 'xs.{0}OrDefault()'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A ternary that checks Any() and then re-walks the sequence with First()/Last() hand-writes exactly what FirstOrDefault()/LastOrDefault() already do in a single pass.");

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
        if (conditional.Condition is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Any", Expression: var anyReceiver },
            } anyInvocation)
        {
            return;
        }

        if (!IsDefaultLike(conditional.WhenFalse)) return;

        if (conditional.WhenTrue is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "First" or "Last", Expression: var terminalReceiver } terminalAccess,
            } terminalInvocation)
        {
            return;
        }
        if (!anyReceiver.IsEquivalentTo(terminalReceiver)) return;

        var anyArgs = anyInvocation.ArgumentList.Arguments;
        var terminalArgs = terminalInvocation.ArgumentList.Arguments;
        if (anyArgs.Count != terminalArgs.Count) return;
        if (anyArgs.Count == 1 && !anyArgs[0].Expression.IsEquivalentTo(terminalArgs[0].Expression)) return;
        if (anyArgs.Count > 1) return;

        var terminalName = terminalAccess.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(anyInvocation, ct).Symbol is not IMethodSymbol { Name: "Any" } anyMethod)
            return;
        if (anyMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(terminalInvocation, ct).Symbol is not IMethodSymbol { Name: var resolvedTerminalName } terminalMethod
            || resolvedTerminalName != terminalName)
        {
            return;
        }
        if (terminalMethod.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, conditional.GetLocation(), terminalName));
    }

    private static bool IsDefaultLike(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression or (int)SyntaxKind.NullLiteralExpression } => true,
        DefaultExpressionSyntax => true,
        _ => false,
    };
}
