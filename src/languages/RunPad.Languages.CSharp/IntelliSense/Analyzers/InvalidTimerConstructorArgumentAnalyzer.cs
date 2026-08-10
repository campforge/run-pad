using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят первый проход, batch) — own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidIndexConstructorArgumentAnalyzer (RPAD0327, same
// проход): `new System.Threading.Timer(callback, state, int dueTime, int period)` throws
// ArgumentOutOfRangeException when `dueTime` or `period` is less than -1 — confirmed empirically via
// scratch `dotnet run` script (`-1` is the documented `Timeout.Infinite` sentinel and is itself valid;
// only values strictly less than -1 throw). Deliberately hand-codes this check instead of using the
// "construct the real object inside a try/catch" technique from RPAD0319/RPAD0328: unlike
// `SemaphoreSlim`/`ManualResetEventSlim` (inert once constructed until explicitly waited on), a live
// `Timer` with a small literal `dueTime` can fire its callback on a thread-pool thread almost
// immediately after construction — probing it for real during analysis would risk actually invoking
// arbitrary user callback code as a side effect of static analysis, which none of this catalog's other
// throwaway-construction probes risk. Scoped to the plain `(TimerCallback, object?, int, int)` overload
// only — the `TimeSpan`/`uint`/`long` overloads have different parameter types and aren't matched by
// this analyzer's all-`int`-in-last-two-positions check.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTimerConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0329";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Timer dueTime/period argument is invalid",
        messageFormat: "'new Timer(...)' always throws ArgumentOutOfRangeException at runtime — {0} ({1}) must be -1 (Timeout.Infinite) or non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new Timer(callback, state, dueTime, period) throws ArgumentOutOfRangeException when dueTime or period is less than -1.");

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
        if (creation.ArgumentList is not { Arguments: [_, _, { Expression: var dueTimeArg }, { Expression: var periodArg }] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(dueTimeArg, ct).Value is not int dueTime) return;
        if (context.SemanticModel.GetConstantValue(periodArg, ct).Value is not int period) return;

        string parameterName;
        int value;
        if (dueTime < -1)
        {
            parameterName = "dueTime";
            value = dueTime;
        }
        else if (period < -1)
        {
            parameterName = "period";
            value = period;
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Threading.Timer") return;
        if (ctor.Parameters is not [_, _, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), parameterName, value));
    }
}
