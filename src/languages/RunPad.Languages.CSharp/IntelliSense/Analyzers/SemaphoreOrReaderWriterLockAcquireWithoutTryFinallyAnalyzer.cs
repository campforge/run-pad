using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "acquire without a guaranteed release" theme as MonitorEnterWithoutTryFinallyAnalyzer (RPAD0228,
// проход 237), for two more synchronization primitives that don't have a `lock`-style keyword of their
// own: `SemaphoreSlim.Wait()`/`WaitAsync()` and `ReaderWriterLockSlim.EnterReadLock()`/
// `EnterWriteLock()`/`EnterUpgradeableReadLock()` all acquire something that MUST be released
// (`Release()`/`ExitReadLock()`/etc.) for other threads to proceed — an exception thrown before that
// release call, with no try/finally guaranteeing it still runs, permanently starves every other thread
// waiting on the same semaphore/lock. Same deliberate narrowness as RPAD0228: only checks that the very
// next statement in the same block is a try/finally, without verifying which release method the finally
// actually calls.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SemaphoreOrReaderWriterLockAcquireWithoutTryFinallyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0230";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Semaphore/ReaderWriterLock acquired without a guaranteed release",
        messageFormat: "'{0}' should be immediately followed by a try/finally that releases it — otherwise an exception before the matching release call starves every other thread waiting on it",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Without a try/finally guaranteeing the matching release call runs, an exception thrown after acquiring a semaphore or reader/writer lock permanently starves every other thread waiting on it.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, ImmutableHashSet<string>> AcquireMethodsByType =
        ImmutableDictionary.CreateRange(
            [
                KeyValuePair.Create("System.Threading.SemaphoreSlim", ImmutableHashSet.Create("Wait", "WaitAsync")),
                KeyValuePair.Create(
                    "System.Threading.ReaderWriterLockSlim",
                    ImmutableHashSet.Create("EnterReadLock", "EnterWriteLock", "EnterUpgradeableReadLock")),
            ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ExpressionStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        var innerExpression = statement.Expression is AwaitExpressionSyntax awaitExpression
            ? awaitExpression.Expression
            : statement.Expression;

        if (innerExpression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName },
            } invocation)
        {
            return;
        }
        if (statement.Parent is not BlockSyntax { Statements: var statements }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType is not { } containingType) return;
        if (!AcquireMethodsByType.TryGetValue(containingType.ToDisplayString(), out var acquireMethods)) return;
        if (!acquireMethods.Contains(methodName)) return;

        var index = statements.IndexOf(statement);
        if (index < 0) return;

        if (index + 1 < statements.Count && statements[index + 1] is TryStatementSyntax { Finally: not null })
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation(), $"{containingType.Name}.{methodName}(...)"));
    }
}
