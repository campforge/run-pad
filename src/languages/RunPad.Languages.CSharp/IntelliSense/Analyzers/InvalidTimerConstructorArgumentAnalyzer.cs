using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidIndexConstructorArgumentAnalyzer (RPAD0327): `new
// System.Threading.Timer(callback, state, int dueTime, int period)` throws
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
// Extended to also cover the INSTANCE method
// `timer.Change(int dueTime, int period)` — confirmed empirically to share the identical `< -1`
// boundary formula and exception type as the constructor. Purely a static syntactic check on the
// literal arguments (same as the constructor branch), so it carries none of the "might invoke the
// callback" risk that ruled out the "run the real validator" technique above — no `Timer` object is
// ever actually constructed or rescheduled by this analyzer.
// Extended again to
// also cover the `(TimerCallback, object?, long dueTime, long period)` constructor overload and the
// matching `timer.Change(long, long)` — explicitly OUT of scope for the original check (its own doc
// comment above says so) since it hard-requires `Int32` parameters. Confirmed empirically the `long`
// overload has a DIFFERENT, two-sided boundary the `int` overload never needs to check: valid range is
// `-1` (the `Timeout.Infinite` sentinel) or `[0, 4294967294]` (0xfffffffe) — values `< -1` OR `>
// 4294967294` both throw `ArgumentOutOfRangeException` (the `int` overload's own range never reaches
// the upper bound, since `int.MaxValue` is well below 4294967294, so this upper-bound case is new).
// Confirmed the sibling `uint` overload has a DIFFERENT, inconsistent boundary (`uint.MaxValue` does
// NOT throw there, unlike the numerically-identical value on the `long` overload) — deliberately NOT
// extended to `uint` given that ambiguity; scoped strictly to `long`. Same non-probing rationale as the
// `int` overload above — a hand-rolled range check, never constructs a real `Timer`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTimerConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0329";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Timer dueTime/period argument is invalid",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — {1} ({2}) must be -1 (Timeout.Infinite) or non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new Timer(callback, state, dueTime, period) and timer.Change(dueTime, period) throw ArgumentOutOfRangeException when dueTime or period is less than -1 (both the int and long overloads), or when it exceeds 4294967294 (the long overload only).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private const long MaxLongDueTimeOrPeriod = 0xfffffffeL;

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeChange, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeChange(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Change" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var dueTimeArg }, { Expression: var periodArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Change" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Threading.Timer") return;

        string? parameterName;
        object? value;
        if (method.Parameters is [{ Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }])
        {
            if (context.SemanticModel.GetConstantValue(dueTimeArg, ct).Value is not int dueTime) return;
            if (context.SemanticModel.GetConstantValue(periodArg, ct).Value is not int period) return;
            (parameterName, value) = GetViolation(dueTime, period);
        }
        else if (method.Parameters is [{ Type.SpecialType: SpecialType.System_Int64 }, { Type.SpecialType: SpecialType.System_Int64 }])
        {
            if (context.SemanticModel.GetConstantValue(dueTimeArg, ct).Value is not long dueTime) return;
            if (context.SemanticModel.GetConstantValue(periodArg, ct).Value is not long period) return;
            (parameterName, value) = GetViolation(dueTime, period);
        }
        else
        {
            return;
        }
        if (parameterName is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), parameterName, value));
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [_, _, { Expression: var dueTimeArg }, { Expression: var periodArg }] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Threading.Timer") return;

        string? parameterName;
        object? value;
        if (ctor.Parameters is [_, _, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }])
        {
            if (context.SemanticModel.GetConstantValue(dueTimeArg, ct).Value is not int dueTime) return;
            if (context.SemanticModel.GetConstantValue(periodArg, ct).Value is not int period) return;
            (parameterName, value) = GetViolation(dueTime, period);
        }
        else if (ctor.Parameters is [_, _, { Type.SpecialType: SpecialType.System_Int64 }, { Type.SpecialType: SpecialType.System_Int64 }])
        {
            if (context.SemanticModel.GetConstantValue(dueTimeArg, ct).Value is not long dueTime) return;
            if (context.SemanticModel.GetConstantValue(periodArg, ct).Value is not long period) return;
            (parameterName, value) = GetViolation(dueTime, period);
        }
        else
        {
            return;
        }
        if (parameterName is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "new Timer(...)", parameterName, value));
    }

    private static (string? ParameterName, object? Value) GetViolation(int dueTime, int period)
    {
        if (dueTime < -1) return ("dueTime", dueTime);
        if (period < -1) return ("period", period);
        return (null, null);
    }

    private static (string? ParameterName, object? Value) GetViolation(long dueTime, long period)
    {
        if (dueTime < -1 || dueTime > MaxLongDueTimeOrPeriod) return ("dueTime", dueTime);
        if (period < -1 || period > MaxLongDueTimeOrPeriod) return ("period", period);
        return (null, null);
    }
}
