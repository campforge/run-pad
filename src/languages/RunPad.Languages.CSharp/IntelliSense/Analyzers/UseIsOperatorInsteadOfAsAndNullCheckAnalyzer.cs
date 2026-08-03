using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 147, batch) — idea ported from Roslynator (Apache License 2.0,
// https://github.com/dotnet/roslynator). `expr as T != null` (or `== null`) is the pre-pattern-matching
// idiom for a type check — `expr is T` says the same thing more directly and doesn't need an
// intermediate cast result at all.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseIsOperatorInsteadOfAsAndNullCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0013";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use 'is' operator instead of 'as' and a null check",
        messageFormat: "'{0} is {1}' expresses this type check more directly than 'as' plus a null check",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "'expr as T != null' is the pre-pattern-matching idiom for a type check — 'expr is T' says the same thing directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        BinaryExpressionSyntax? asExpression = Unwrap(binary.Left) as BinaryExpressionSyntax;
        var otherSide = binary.Right;
        if (asExpression is not { RawKind: (int)SyntaxKind.AsExpression })
        {
            asExpression = Unwrap(binary.Right) as BinaryExpressionSyntax;
            otherSide = binary.Left;
        }

        if (asExpression is not { RawKind: (int)SyntaxKind.AsExpression }) return;
        if (!Unwrap(otherSide).IsKind(SyntaxKind.NullLiteralExpression)) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, binary.GetLocation(), asExpression.Left.ToString(), asExpression.Right.ToString()));
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) =>
        expression is ParenthesizedExpressionSyntax parenthesized ? Unwrap(parenthesized.Expression) : expression;
}
