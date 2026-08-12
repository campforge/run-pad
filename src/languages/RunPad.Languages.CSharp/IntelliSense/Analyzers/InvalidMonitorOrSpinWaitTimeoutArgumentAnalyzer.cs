using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят второй проход, batch) — own pick, not a Roslynator port. Sibling of
// InvalidTimeoutArgumentAnalyzer (RPAD0330, same проход) — same "millisecondsTimeout must be -1 or
// non-negative" formula, but on the two STATIC methods that take the timeout as their SECOND argument
// rather than as an instance-method receiver's first argument: `Monitor.TryEnter(object obj, int
// millisecondsTimeout[, ref bool lockTaken])` and `SpinWait.SpinUntil(Func<bool> condition, int
// millisecondsTimeout)` — confirmed empirically to throw ArgumentOutOfRangeException for the identical
// `< -1` boundary. Kept as a separate analyzer from RPAD0330 purely because of this shape difference
// (which argument position holds the timeout), not because the validation logic differs.
// Extended (триста шестьдесят первый проход, batch) to also cover the STATIC `Task.WaitAll(Task[]
// tasks, int millisecondsTimeout)`/`Task.WaitAny(Task[] tasks, int millisecondsTimeout)` and
// `WaitHandle.WaitAll(WaitHandle[] waitHandles, int millisecondsTimeout)`/`WaitHandle.WaitAny(...)` —
// confirmed empirically to share the identical `< -1` boundary formula and the same "array first,
// timeout second" argument shape as `Monitor.TryEnter`/`SpinWait.SpinUntil`. `WaitAll`/`WaitAny` are
// ambiguous by name alone (could resolve to either `Task` or `WaitHandle`), so the containing-type
// check now accepts either for those two method names specifically.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0331";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Timeout argument is less than -1",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — millisecondsTimeout ({1}) must be -1 (Timeout.Infinite) or non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Monitor.TryEnter, SpinWait.SpinUntil, Task.WaitAll/WaitAny, and WaitHandle.WaitAll/WaitAny throw ArgumentOutOfRangeException when the millisecondsTimeout argument is less than -1.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TaskOrWaitHandleTypes = ImmutableHashSet.Create(
        "System.Threading.Tasks.Task", "System.Threading.WaitHandle");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "TryEnter" or "SpinUntil" or "WaitAll" or "WaitAny" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var timeoutArg }, ..]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(timeoutArg, ct).Value is not int timeout || timeout >= -1)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.Parameters is not [_, { Type.SpecialType: SpecialType.System_Int32 }, ..]) return;

        var containingTypeName = method.ContainingType?.ToDisplayString();
        var matches = methodName switch
        {
            "TryEnter" => containingTypeName == "System.Threading.Monitor",
            "SpinUntil" => containingTypeName == "System.Threading.SpinWait",
            "WaitAll" or "WaitAny" => containingTypeName is not null && TaskOrWaitHandleTypes.Contains(containingTypeName),
            _ => false,
        };
        if (!matches) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), timeout));
    }
}
