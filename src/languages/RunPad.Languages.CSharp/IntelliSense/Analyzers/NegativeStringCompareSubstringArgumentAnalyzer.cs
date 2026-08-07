using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок пятый проход, batch) — own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeListIndexOrCountArgumentAnalyzer (RPAD0287): the
// static `string.Compare(string strA, int indexA, string strB, int indexB, int length[, ...])`
// substring-comparison overloads throw ArgumentOutOfRangeException when `indexA`, `indexB`, or `length`
// is negative — confirmed empirically via scratch `dotnet run` script, not assumed. Deliberately does
// NOT attempt the "index exceeds receiver length" case (unlike RPAD0271/RPAD0294's array/string-bounds
// family) — the same empirical check found that `Compare` does NOT require `index + length` to stay
// within the receiver's bounds at all (excess `length` is silently truncated to whatever characters are
// actually available, confirmed by `string.Compare("abc", 3, "def", 0, 1)` — index at the string's own
// length with a length that would read past the end — NOT throwing), so there is no meaningful "known
// length" check to add here; only negativity is ever provably wrong. This scoping mirrors RPAD0287's own
// scope exactly (negativity only, no receiver-length dependency, works even without a literal receiver).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeStringCompareSubstringArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0311";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative string.Compare substring argument always throws",
        messageFormat: "'string.Compare(...)' always throws ArgumentOutOfRangeException at runtime — {0} cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.Compare(strA, indexA, strB, indexB, length[, ...]) throws ArgumentOutOfRangeException when indexA, indexB, or length is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Compare" })
            return;
        if (invocation.ArgumentList.Arguments.Count < 5) return;
        var arguments = invocation.ArgumentList.Arguments;

        string? argName = null;
        if (IsNegativeLiteral(arguments[1].Expression)) argName = "indexA";
        else if (IsNegativeLiteral(arguments[3].Expression)) argName = "indexB";
        else if (IsNegativeLiteral(arguments[4].Expression)) argName = "length";
        if (argName is null) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: true, Name: "Compare" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;
        if (method.Parameters is not
            [
            { Type.SpecialType: SpecialType.System_String },
            { Name: "indexA", Type.SpecialType: SpecialType.System_Int32 },
            { Type.SpecialType: SpecialType.System_String },
            { Name: "indexB", Type.SpecialType: SpecialType.System_Int32 },
            { Name: "length", Type.SpecialType: SpecialType.System_Int32 },
                ..
            ])
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), argName));
    }

    private static bool IsNegativeLiteral(ExpressionSyntax expression) =>
        expression is PrefixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.UnaryMinusExpression,
            Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression },
        };
}
