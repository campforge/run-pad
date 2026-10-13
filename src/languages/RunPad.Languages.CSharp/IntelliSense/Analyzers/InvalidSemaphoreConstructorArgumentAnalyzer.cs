using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidSemaphoreSlimConstructorArgumentAnalyzer
// (RPAD0319, проход 348), applied to `System.Threading.Semaphore(int initialCount, int
// maximumCount)` instead of `SemaphoreSlim` — confirmed empirically to throw
// ArgumentOutOfRangeException for a negative `initialCount` or a `maximumCount` that isn't strictly
// positive, but a DIFFERENT exception type, ArgumentException, when `initialCount` exceeds a
// positive `maximumCount` (unlike SemaphoreSlim, which throws ArgumentOutOfRangeException for all
// three conditions) — confirmed via scratch `dotnet run` script, not assumed. Deliberately hand-codes
// this validation instead of reusing the "construct the real object inside a try/catch" technique
// from RPAD0314/RPAD0319/RPAD0313: unlike SemaphoreSlim (a managed-only construct) or TimeSpan/DateTime
// (structs), an unnamed `Semaphore` wraps a real OS kernel handle from the moment it's constructed, so
// probing it live during analysis would leak a real system handle on every incremental re-analysis
// (never disposed, same as this catalog's other throwaway probes) — an acceptable tradeoff for a
// managed-only throwaway, but not for one backed by a live OS resource. Scoped to the 2-arg
// `(initialCount, maximumCount)` overload only — the 3-/4-/5-arg overloads take a `string? name` and
// create a cross-process NAMED kernel object, out of scope for a "literal always throws" check anyway
// since name collisions depend on OS-level state outside the source file.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSemaphoreConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0322";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Semaphore constructor arguments are invalid",
        messageFormat: "'new Semaphore(...)' always throws {0} at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new Semaphore(initialCount, maximumCount) throws ArgumentOutOfRangeException when initialCount is negative or maximumCount is not positive, and ArgumentException when initialCount exceeds maximumCount.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var initialArg }, { Expression: var maxArg }] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(initialArg, ct).Value is not int initialCount) return;
        if (context.SemanticModel.GetConstantValue(maxArg, ct).Value is not int maximumCount) return;

        string? exceptionType;
        string? reason;
        if (initialCount < 0)
        {
            exceptionType = "ArgumentOutOfRangeException";
            reason = "initialCount cannot be negative";
        }
        else if (maximumCount < 1)
        {
            exceptionType = "ArgumentOutOfRangeException";
            reason = "maximumCount must be positive";
        }
        else if (initialCount > maximumCount)
        {
            exceptionType = "ArgumentException";
            reason = $"initialCount ({initialCount}) cannot exceed maximumCount ({maximumCount})";
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Threading.Semaphore") return;
        if (!ctor.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), exceptionType, reason));
    }
}
