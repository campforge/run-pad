using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести тридцать третий проход, batch) — own pick, not a Roslynator port.
// `if (x == null) throw new ArgumentNullException(nameof(x));` (or the `if (x is null)` spelling) is
// the hand-written guard-clause idiom that predates `ArgumentNullException.ThrowIfNull(object?, string?
// = null)` (.NET 6+, always available given RunPad's own SDK floor,
// DotnetSdkProvider.MinSupportedSdkMajorVersion) — the one-line static helper does the same null check
// and throw, with the caller-argument-expression captured automatically instead of spelled out via
// nameof. Deliberately narrow: only the plain `new ArgumentNullException(nameof(x))` constructor call
// with exactly one argument (matching the checked identifier) — a custom message argument has no
// equivalent parameter on ThrowIfNull, so that shape is left alone rather than guessed at.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferThrowIfNullOverIfNullCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0223";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer ArgumentNullException.ThrowIfNull over a hand-written null guard",
        messageFormat: "'if (x == null) throw new ArgumentNullException(nameof(x))' can be replaced with 'ArgumentNullException.ThrowIfNull(x)'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ArgumentNullException.ThrowIfNull(x) does the same null check and throw as the hand-written guard clause, in one call.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.IfStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not null) return;

        if (!TryGetCheckedIdentifier(ifStatement.Condition, out var checkedIdentifier)) return;

        var throwStatement = ifStatement.Statement switch
        {
            ThrowStatementSyntax direct => direct,
            BlockSyntax { Statements: [ThrowStatementSyntax blockThrow] } => blockThrow,
            _ => null,
        };
        if (throwStatement?.Expression is not ObjectCreationExpressionSyntax
            {
                Type: IdentifierNameSyntax { Identifier.Text: "ArgumentNullException" },
                ArgumentList.Arguments: [{ Expression: InvocationExpressionSyntax nameofInvocation }],
            } creation)
        {
            return;
        }
        if (nameofInvocation.Expression is not IdentifierNameSyntax { Identifier.Text: "nameof" }) return;
        if (nameofInvocation.ArgumentList.Arguments is not [{ Expression: IdentifierNameSyntax nameofTarget }])
            return;
        if (nameofTarget.Identifier.Text != checkedIdentifier.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.ArgumentNullException") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.GetLocation()));
    }

    private static bool TryGetCheckedIdentifier(ExpressionSyntax condition, out IdentifierNameSyntax identifier)
    {
        identifier = null!;
        switch (condition)
        {
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression, Left: IdentifierNameSyntax left, Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } }:
                identifier = left;
                return true;
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression, Left: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }, Right: IdentifierNameSyntax right }:
                identifier = right;
                return true;
            case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } }, Expression: IdentifierNameSyntax patternTarget }:
                identifier = patternTarget;
                return true;
            default:
                return false;
        }
    }
}
