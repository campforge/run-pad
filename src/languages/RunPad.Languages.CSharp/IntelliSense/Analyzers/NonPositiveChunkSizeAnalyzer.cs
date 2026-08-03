using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести сорок пятый проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as the NegativeCapacityArgumentAnalyzer (RPAD0231) family, for a
// different threshold: `xs.Chunk(size)` is documented to throw `ArgumentOutOfRangeException` when
// `size` is below 1 — unlike the other analyzers in this family, ZERO also throws here (a chunk size of
// zero is meaningless), so the check is "less than one", not just "negative".
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NonPositiveChunkSizeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0240";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Chunk with a non-positive size always throws ArgumentOutOfRangeException",
        messageFormat: "'xs.Chunk({0})' always throws ArgumentOutOfRangeException at runtime — size must be at least 1",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.Chunk throws ArgumentOutOfRangeException when size is less than 1.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Chunk" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var sizeArg }]) return;
        if (!TryGetIntLiteralValue(sizeArg, out var value)) return;
        if (value >= 1) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Chunk" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }

    private static bool TryGetIntLiteralValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } negativeLiteral })
        {
            if (negativeLiteral.Token.Value is not int positiveValue) return false;
            value = -positiveValue;
            return true;
        }
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal && literal.Token.Value is int literalValue)
        {
            value = literalValue;
            return true;
        }
        return false;
    }
}
