using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231): the static
// `System.Security.Cryptography.RandomNumberGenerator.GetBytes(int count)` and
// `GetHexString(int count[, bool lowercase])` both throw ArgumentOutOfRangeException for a negative
// `count` — confirmed empirically via scratch `dotnet run` script, not assumed (also confirmed
// `count == 0` does NOT throw for either — an empty result is valid). Deliberately does NOT cover
// `RandomNumberGenerator.GetInt32` — considered alongside this and found to have a DIFFERENT, stricter
// boundary (`GetInt32(0)` ALSO throws — the exclusive upper bound must be strictly positive, not merely
// non-negative — confirmed empirically), plus a 2-argument `fromInclusive`/`toInclusive` overload with
// its own ordering constraint; left as a separate, more involved future analyzer rather than shoehorned
// into this negative-only check. Scoped to a LITERAL negative integer only, same reasoning as the rest
// of this family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeRandomNumberGeneratorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0304";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative RandomNumberGenerator count argument always throws",
        messageFormat: "'RandomNumberGenerator.{0}({1})' always throws ArgumentOutOfRangeException at runtime — count cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RandomNumberGenerator.GetBytes(int count) and GetHexString(int count[, bool lowercase]) both throw ArgumentOutOfRangeException when count is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetBytes" or "GetHexString" })
            return;
        var methodName = ((MemberAccessExpressionSyntax)invocation.Expression).Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var countArg }, ..]) return;
        if (!TryGetNegativeIntValue(countArg, out var value)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Security.Cryptography.RandomNumberGenerator") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, value));
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
