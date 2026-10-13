using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Probed
// empirically against the built-in "Null check can be simplified" analyzer first — that one already
// covers the ternary/conditional-EXPRESSION shape (`x == null ? fallback : x.Member`, blocked an idea
// in this same проход), but does NOT extend to this IF-STATEMENT shape: `if (x != null) { x.Foo(); }`
// with a single-statement body and no else calls `x.Foo()` only when `x` isn't null — exactly what
// `x?.Foo();` already says directly, without the surrounding block.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferNullConditionalOverIfNullCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0146";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the null-conditional operator over a single-statement null-check guard",
        messageFormat: "'if (x != null) x.Member(...);' can be simplified to 'x?.Member(...);'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An if statement that only null-checks a value before calling a member on it, with no else, says the same thing as the null-conditional operator.");

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

        if (ifStatement.Condition is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.NotEqualsExpression,
                Left: IdentifierNameSyntax conditionId,
                Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression },
            })
        {
            return;
        }

        var body = ifStatement.Statement is BlockSyntax { Statements: [var only] } ? only : ifStatement.Statement;
        if (body is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax memberReceiverId },
                },
            })
        {
            return;
        }

        if (conditionId.Identifier.Text != memberReceiverId.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (!SymbolEqualityComparer.Default.Equals(
                context.SemanticModel.GetSymbolInfo(conditionId, ct).Symbol,
                context.SemanticModel.GetSymbolInfo(memberReceiverId, ct).Symbol))
        {
            return;
        }

        var receiverType = context.SemanticModel.GetTypeInfo(conditionId, ct).Type;
        if (receiverType is not { IsValueType: false }) return; // ?. needs a reference type or Nullable<T>, keep this simple

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.GetLocation()));
    }
}
