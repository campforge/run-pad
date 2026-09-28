using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста двадцать шестой проход, batch) — own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer/NegativeCountArgumentAnalyzer
// (RPAD0231/RPAD0232): `System.Buffers.ArrayPool<T>.Shared.Rent(int minimumLength)` throws
// ArgumentOutOfRangeException when `minimumLength` is negative — confirmed empirically via scratch
// `dotnet run` script, not assumed (also confirmed `Rent(0)` does NOT throw — zero is a valid, if
// pointless, request — so the check is strictly `< 0`, same boundary as the rest of this family).
// Scoped to a LITERAL negative integer only, same reasoning as the rest of this family. Matched by
// containing type `System.Buffers.ArrayPool<T>` (its `OriginalDefinition`) rather than by the `Shared`
// property specifically, since `Rent` is an ordinary instance method — any `ArrayPool<T>` instance (not
// just `.Shared`) has the same validation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeArrayPoolRentLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0295";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative ArrayPool<T>.Rent length always throws ArgumentOutOfRangeException",
        messageFormat: "'ArrayPool<T>.Rent({0})' always throws ArgumentOutOfRangeException at runtime — minimumLength cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "System.Buffers.ArrayPool<T>.Rent(int minimumLength) throws ArgumentOutOfRangeException when minimumLength is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Rent" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var lengthArg }])
            return;
        if (!TryGetNegativeIntValue(lengthArg, out var value)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Rent" } method)
            return;
        if (method.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Buffers.ArrayPool<T>") return;

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
