using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят второй проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCountArgumentAnalyzer (RPAD0232), applied to
// `SemaphoreSlim.Release(int releaseCount)` (and the non-generic `System.Threading.Semaphore.Release(int)`,
// identical formula) instead of a LINQ method: throws ArgumentOutOfRangeException when `releaseCount`
// isn't strictly positive — confirmed empirically via scratch `dotnet run` script. Not a duplicate of
// RPAD0232 (that analyzer is scoped to `Enumerable.XXX(..., count)` extension methods only) nor of
// RPAD0322/RPAD0319 (which validate the CONSTRUCTOR, not the `Release` instance method). Doesn't attempt
// to track whether a literal `releaseCount` would exceed the semaphore's own `maxCount` — that requires
// following the semaphore instance's construction site, out of scope for a single-call literal check.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSemaphoreSlimReleaseArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0334";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Semaphore/SemaphoreSlim.Release argument is not positive",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — releaseCount must be positive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SemaphoreSlim.Release(releaseCount) and Semaphore.Release(releaseCount) throw ArgumentOutOfRangeException when releaseCount isn't strictly positive.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Release" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var countArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int releaseCount || releaseCount >= 1)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Release" } method)
            return;
        if (method.ContainingType?.ToDisplayString() is not ("System.Threading.SemaphoreSlim" or "System.Threading.Semaphore"))
            return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString()));
    }
}
