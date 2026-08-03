using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести сорок второй проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as InvalidRandomRangeArgumentAnalyzer (RPAD0234)/
// SubstringOutOfRangeAnalyzer (RPAD0235, проход 241): `xs.ElementAt(index)` is documented to throw
// `ArgumentOutOfRangeException` when `index` is negative — a mistake the compiler can't catch on its
// own. Scoped to a LITERAL negative integer, same reasoning as the rest of this family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeElementAtIndexAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0236";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ElementAt with a negative index always throws ArgumentOutOfRangeException",
        messageFormat: "'xs.ElementAt({0})' always throws ArgumentOutOfRangeException at runtime — index cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.ElementAt throws ArgumentOutOfRangeException when the index is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ElementAt" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var indexArg }])
            return;
        if (!TryGetNegativeIntValue(indexArg, out var value)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ElementAt" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }

    private static bool TryGetNegativeIntValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
            })
        {
            return false;
        }
        if (literal.Token.Value is not int positiveValue) return false;

        value = -positiveValue;
        return true;
    }
}
