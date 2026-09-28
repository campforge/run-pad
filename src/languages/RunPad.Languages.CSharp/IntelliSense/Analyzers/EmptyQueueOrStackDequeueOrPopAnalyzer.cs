using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста девятнадцатый проход, batch) — own pick, not a Roslynator port. Same "provably
// empty receiver" theme as EmptyArrayLiteralFirstOrSingleOrLastAnalyzer (RPAD0290, проходы 317-318),
// applied to `Queue<T>.Dequeue()`/`Peek()` and `Stack<T>.Pop()`/`Peek()` instead of array literals:
// calling any of them directly on a FRESHLY CONSTRUCTED `Queue<T>`/`Stack<T>` (any constructor — the
// empty default one, or `Queue<T>(int capacity)`/`Stack<T>(int capacity)`, both of which only reserve
// capacity without adding elements) always throws InvalidOperationException, since the receiver is
// provably empty at that point in the expression — confirmed empirically via scratch `dotnet run`
// script, not assumed. Deliberately scoped to a call chained DIRECTLY off the constructor call
// (`new Queue<T>().Dequeue()`), not a variable that might have had elements added to it since
// construction — proving THAT would need real data-flow analysis, same scoping discipline as the rest of
// this catalog.
// Extended in the триста двадцатый проход to also cover `PriorityQueue<TElement, TPriority>.Dequeue()`/
// `Peek()` and `LinkedList<T>.RemoveFirst()`/`RemoveLast()` — confirmed empirically to throw identically
// for a freshly constructed, empty receiver. Dispatch is now keyed by the containing type's simple name
// (`Queue`/`Stack`/`PriorityQueue`/`LinkedList`) rather than its full generic display string, which
// avoids brittleness against differing type-parameter arity (`Queue<T>` has one type parameter,
// `PriorityQueue<TElement, TPriority>` has two).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyQueueOrStackDequeueOrPopAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0291";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Dequeue/Pop/Peek/RemoveFirst/RemoveLast on a freshly constructed empty collection always throws",
        messageFormat: "'new {0}(...).{1}()' always throws InvalidOperationException at runtime — the receiver is freshly constructed and has no elements",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Queue<T>/PriorityQueue<TElement, TPriority>.Dequeue()/Peek(), Stack<T>.Pop()/Peek(), and LinkedList<T>.RemoveFirst()/RemoveLast() throw InvalidOperationException when called on an empty collection — provably true when the receiver is a freshly constructed instance with no elements added.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, ImmutableHashSet<string>> ValidMethodNamesByTypeName =
        ImmutableDictionary.CreateRange(
            [
                KeyValuePair.Create("Queue", ImmutableHashSet.Create("Dequeue", "Peek")),
                KeyValuePair.Create("Stack", ImmutableHashSet.Create("Pop", "Peek")),
                KeyValuePair.Create("PriorityQueue", ImmutableHashSet.Create("Dequeue", "Peek")),
                KeyValuePair.Create("LinkedList", ImmutableHashSet.Create("RemoveFirst", "RemoveLast")),
            ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Dequeue" or "Peek" or "Pop" or "RemoveFirst" or "RemoveLast",
                Expression: BaseObjectCreationExpressionSyntax receiverCreation,
            } access)
        {
            return;
        }
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.OriginalDefinition is not { } containingType
            || containingType.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic")
        {
            return;
        }
        var typeKeyword = containingType.Name;
        if (!ValidMethodNamesByTypeName.TryGetValue(typeKeyword, out var validMethods) || !validMethods.Contains(methodName))
            return;

        if (context.SemanticModel.GetSymbolInfo(receiverCreation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
            || ctor.ContainingType?.OriginalDefinition.Name != typeKeyword
            || ctor.ContainingType.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), typeKeyword, methodName));
    }
}
