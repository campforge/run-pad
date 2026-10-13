using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor argument validation" theme extended to instance methods: `WaitHandle.WaitOne(int
// millisecondsTimeout[, bool])`, `Task.Wait(int millisecondsTimeout[, CancellationToken])`,
// `Thread.Join(int millisecondsTimeout)`, and `ManualResetEventSlim.Wait(int millisecondsTimeout[,
// CancellationToken])` all throw ArgumentOutOfRangeException for a `millisecondsTimeout` less than -1
// (the documented `Timeout.Infinite` sentinel, itself valid) — confirmed empirically via scratch
// `dotnet run` script that all four share the IDENTICAL boundary formula, just as an INSTANCE method
// with the timeout as the first explicit argument (unlike the STATIC `Monitor.TryEnter`/
// `SpinWait.SpinUntil`, which take the timeout as a later argument — see the sibling
// InvalidMonitorOrSpinWaitTimeoutArgumentAnalyzer, RPAD0331, same change, kept separate for that
// shape difference). `WaitHandle.WaitOne`/`Task.Wait`/`ManualResetEventSlim.Wait` are resolved via the
// BASE type that actually declares the method (`WaitHandle`/`Task`/`ManualResetEventSlim` itself) since
// Roslyn resolves an inherited call to the declaring symbol regardless of the concrete receiver type
// (e.g. `ManualResetEvent`/`Semaphore`/`Mutex` all inherit `WaitOne` from `WaitHandle` unchanged).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTimeoutArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0330";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Timeout argument is less than -1",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — millisecondsTimeout ({1}) must be -1 (Timeout.Infinite) or non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "WaitHandle.WaitOne, Task.Wait, Thread.Join, and ManualResetEventSlim.Wait throw ArgumentOutOfRangeException when the millisecondsTimeout argument is less than -1.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "WaitOne" or "Wait" or "Join" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var timeoutArg }, ..]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(timeoutArg, ct).Value is not int timeout || timeout >= -1)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }, ..]) return;

        var expectedType = methodName switch
        {
            "WaitOne" => "System.Threading.WaitHandle",
            "Wait" => "System.Threading.Tasks.Task",
            "Join" => "System.Threading.Thread",
            _ => null,
        };
        var containingTypeName = method.ContainingType?.OriginalDefinition.ToDisplayString();
        var matches = containingTypeName == expectedType
            || (methodName == "Wait" && containingTypeName == "System.Threading.ManualResetEventSlim");
        if (!matches) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), timeout));
    }
}
