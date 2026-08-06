using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок третий проход, batch) — own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231): every
// `string.Split(...)` overload that accepts a `count` parameter (separator as `char`/`char[]`/`string`/
// `string[]`, with or without `StringSplitOptions`) throws ArgumentOutOfRangeException for a negative
// `count` — confirmed empirically via scratch `dotnet run` script, not assumed, across every separator
// shape. Confirmed via reflection (not assumed) that every one of these overloads names this parameter
// `count` and types it `int`, always at the same ordinal position (right after the separator, before any
// `StringSplitOptions`) — matched by parameter NAME/TYPE at that fixed position rather than by argument
// count alone, which is what correctly excludes the sibling `Split(separator, StringSplitOptions)`
// overloads (same argument count, but the second parameter there is `StringSplitOptions`, not `int`).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeStringSplitCountArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0307";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative string.Split count argument always throws",
        messageFormat: "'{0}.Split(...)' always throws ArgumentOutOfRangeException at runtime — count cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every string.Split(...) overload that accepts a count parameter throws ArgumentOutOfRangeException when count is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Split" } access)
            return;
        if (invocation.ArgumentList.Arguments.Count < 2) return;
        var countArg = invocation.ArgumentList.Arguments[1].Expression;
        if (!TryGetNegativeIntValue(countArg, out _)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Split" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;
        if (method.Parameters is not [_, { Name: "count", Type.SpecialType: SpecialType.System_Int32 }, ..]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString()));
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
