using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the fourteenth research subagent survey,
// re-verified independently before implementing. `Task.ContinueWith(..., TaskContinuationOptions)` — a
// self-conflicting `TaskContinuationOptions` combination throws `ArgumentOutOfRangeException`, the first
// "enum FLAGS combination" analyzer in this catalog targeting `TaskContinuationOptions` specifically (a
// genuinely different type from the three flags enums `InvalidDateTimeStylesCombinationAnalyzer`/
// `InvalidRegexOptionsCombinationAnalyzer`/`InvalidNumberStylesCombinationAnalyzer` already cover).
// Confirmed empirically, two distinct conflict families: combining any two of `NotOnRanToCompletion`/
// `NotOnFaulted`/`NotOnCanceled` with the OPPOSITE `OnlyOnXxx` flag for a DIFFERENT kind (or combining
// two different `OnlyOnXxx` flags together) throws with the message "excluded all continuation kinds";
// combining `LongRunning` with `ExecuteSynchronously` throws a SEPARATE message ("combined LongRunning
// and ExecuteSynchronously"). Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. Probed against a
// NEVER-COMPLETING antecedent (`new TaskCompletionSource<bool>().Task`, never `SetResult`) rather than
// `Task.CompletedTask` — confirmed empirically this still throws identically for an invalid combination
// (the validation is synchronous and runs before the antecedent's completion state is even inspected),
// but for a VALID combination the registered continuation delegate is provably never invoked (confirmed
// by waiting past it and observing no execution) — avoiding the real thread-pool work item that
// `Task.CompletedTask.ContinueWith(...)` would otherwise schedule for every valid combination the user
// writes, on every analyzer pass during live editing (same class of side-effect caution
// `InvalidCancellationTokenSourceTimeSpanDelayAnalyzer`, RPAD0280, already established for `Task.Delay`).
// Deliberately scoped ONLY to `TaskContinuationOptions` on `ContinueWith` — confirmed empirically the
// sibling `TaskCreationOptions` (used by `Task.Factory.StartNew`/`new Task(...)`) has no
// `ExecuteSynchronously` member and does NOT reject combinations like `LongRunning |
// RunContinuationsAsynchronously` or `AttachedToParent | DenyChildAttach`, so this analyzer must not be
// extended to those call shapes.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTaskContinuationOptionsCombinationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0452";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TaskContinuationOptions combination always throws",
        messageFormat: "'{0}.ContinueWith(...)' always throws ArgumentOutOfRangeException at runtime — this TaskContinuationOptions combination is not valid together",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Task.ContinueWith(..., TaskContinuationOptions) throws ArgumentOutOfRangeException for a self-conflicting combination (e.g. two mutually exclusive NotOnXxx/OnlyOnXxx kind filters, or LongRunning combined with ExecuteSynchronously).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ContinueWith" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, .., { Expression: var optionsArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(optionsArg, ct).Value is not int rawOptions) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ContinueWith",
                ContainingType: { Name: "Task" } containingType,
                Parameters: [.., { Type.Name: "TaskContinuationOptions" }],
            })
        {
            return;
        }
        if (containingType.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks") return;

        var receiverTypeName = context.SemanticModel.GetTypeInfo(GetReceiverExpression(invocation), ct).Type?.ToDisplayString();
        var receiverDisplay = receiverTypeName?.StartsWith("System.Threading.Tasks.Task<", System.StringComparison.Ordinal) == true ? "Task<TResult>" : "Task";

        if (!AlwaysThrows((TaskContinuationOptions)rawOptions)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), receiverDisplay));
    }

    private static ExpressionSyntax GetReceiverExpression(InvocationExpressionSyntax invocation) =>
        ((MemberAccessExpressionSyntax)invocation.Expression).Expression;

    private static bool AlwaysThrows(TaskContinuationOptions options)
    {
        try
        {
            // Never call SetResult/SetException/SetCanceled on this — an antecedent that never
            // completes means the registered continuation delegate is provably never invoked, avoiding
            // the real thread-pool work item `Task.CompletedTask.ContinueWith(...)` would otherwise
            // schedule for every valid combination, on every analyzer pass during live editing.
            var neverCompletes = new TaskCompletionSource<bool>();
            _ = neverCompletes.Task.ContinueWith(_ => { }, options);
            return false;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
