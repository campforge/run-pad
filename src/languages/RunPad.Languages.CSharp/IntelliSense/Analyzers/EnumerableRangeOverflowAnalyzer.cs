using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Complements
// NegativeCountArgumentAnalyzer (RPAD0232, проход 240), which only covers `Enumerable.Range(start,
// count)`'s negative-`count` throw — `Range` also throws ArgumentOutOfRangeException when `start + count
// - 1` overflows `int.MaxValue`, a completely different (and non-negative) failure mode, confirmed
// empirically via scratch `dotnet run` script (not assumed) to happen EAGERLY at the `Range` call itself
// (no enumeration needed — `Enumerable.Range` validates before returning its lazy iterator), which makes
// it safe and cheap to call the REAL `Enumerable.Range` as the validator here, same "run the real
// validator" technique as the rest of this catalog. Both arguments are read via `GetConstantValue` (not
// just direct numeric-literal tokens), so well-known constants like `int.MaxValue` resolve too.
// Deliberately scoped to a non-negative `count` only — a negative count is RPAD0232's territory, kept
// disjoint by construction rather than by double-checking for it here (RPAD0232 fires on the same call for
// a different reason when count is negative, so this analyzer's own scope simply excludes that case
// rather than colliding with it).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EnumerableRangeOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0284";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Enumerable.Range(start, count) overflows Int32.MaxValue",
        messageFormat: "'Enumerable.Range({0}, {1})' always throws ArgumentOutOfRangeException at runtime — start + count - 1 overflows Int32.MaxValue",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Enumerable.Range(start, count) throws ArgumentOutOfRangeException when start + count - 1 would exceed Int32.MaxValue, even though both arguments individually fit in an int.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Range" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var startArg }, { Expression: var countArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(startArg, ct).Value is not int start) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count || count < 0) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Range" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (!AlwaysThrows(start, count)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), start, count));
    }

    private static bool AlwaysThrows(int start, int count)
    {
        try
        {
            _ = Enumerable.Range(start, count);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
