using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок второй проход, batch) — own pick, not a Roslynator port. Same shape as
// InvalidRandomRangeArgumentAnalyzer (RPAD0234, Random.Next/NextInt64's 1-/2-argument range validation),
// applied to `System.Security.Cryptography.RandomNumberGenerator.GetInt32` instead — deliberately a
// SEPARATE analyzer rather than an extension of RPAD0234 (or RPAD0304, this project's own
// RandomNumberGenerator.GetBytes/GetHexString negative-count analyzer from this same проход), because
// both the boundary AND the exception type differ from each: the 1-argument `GetInt32(int toExclusive)`
// requires `toExclusive` to be STRICTLY POSITIVE (`0` itself throws, unlike `Random.Next(0)`, which is
// valid and simply returns `0`) — confirmed empirically via scratch `dotnet run` script, not assumed.
// The 2-argument `GetInt32(int fromInclusive, int toInclusive)` throws a DIFFERENT exception type,
// `ArgumentException` (not `ArgumentOutOfRangeException`), when `fromInclusive >= toInclusive` — also
// confirmed empirically. Scoped to LITERAL integer arguments only, same reasoning as the rest of this
// family — proving an arbitrary expression's value or relative order needs real data-flow analysis.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRandomNumberGeneratorGetInt32ArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0306";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "RandomNumberGenerator.GetInt32 called with an invalid range",
        messageFormat: "'RandomNumberGenerator.GetInt32({0})' always throws {1} at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RandomNumberGenerator.GetInt32(toExclusive) throws ArgumentOutOfRangeException when toExclusive is not positive; GetInt32(fromInclusive, toInclusive) throws ArgumentException when fromInclusive is not less than toInclusive.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetInt32" })
            return;
        var arguments = invocation.ArgumentList.Arguments;

        string argsText;
        string exceptionType;
        string reason;
        if (arguments is [{ Expression: var toExclusiveArg }])
        {
            if (!TryGetIntLiteralValue(toExclusiveArg, out var toExclusive) || toExclusive > 0) return;
            argsText = toExclusive.ToString();
            exceptionType = "ArgumentOutOfRangeException";
            reason = "toExclusive must be positive";
        }
        else if (arguments is [{ Expression: var fromArg }, { Expression: var toArg }])
        {
            if (!TryGetIntLiteralValue(fromArg, out var fromInclusive)) return;
            if (!TryGetIntLiteralValue(toArg, out var toInclusive)) return;
            if (fromInclusive < toInclusive) return;
            argsText = $"{fromInclusive}, {toInclusive}";
            exceptionType = "ArgumentException";
            reason = "fromInclusive must be less than toInclusive";
        }
        else
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, Name: "GetInt32" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Security.Cryptography.RandomNumberGenerator") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), argsText, exceptionType, reason));
    }

    private static bool TryGetIntLiteralValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } negLiteral,
            })
        {
            if (negLiteral.Token.Value is not int positive) return false;
            value = -positive;
            return true;
        }
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
            && literal.Token.Value is int positiveValue)
        {
            value = positiveValue;
            return true;
        }
        return false;
    }
}
