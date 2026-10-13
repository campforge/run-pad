using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231, the constructor
// form), on the instance METHOD `EnsureCapacity(int capacity)` that `List<T>`/
// `Dictionary<TKey,TValue>`/`HashSet<T>`/`Queue<T>`/`Stack<T>` all expose (.NET Core 2.4+/3.0+, within
// this project's SDK floor) — confirmed empirically via scratch `dotnet run` script, not assumed, that
// all five throw `ArgumentOutOfRangeException` for a negative `capacity` (and that `EnsureCapacity(0)`
// does NOT throw — zero is a valid, if pointless, request). Scoped to a LITERAL negative integer only,
// same reasoning as the rest of this family.
// Extended in the триста тридцать седьмой проход to also cover `TrimExcess(int capacity)` —
// confirmed via reflection (not assumed) that `Dictionary<TKey,TValue>`/`HashSet<T>`/`Queue<T>`/
// `Stack<T>` all expose this overload alongside the parameterless `TrimExcess()` — only `List<T>` does
// NOT (it has just the parameterless form) — and confirmed empirically that the four which do have it
// throw identically to `EnsureCapacity` for a negative `capacity`. The parameterless `TrimExcess()`
// overload is naturally excluded by the argument-count check. `TargetTypes` for this method name is
// therefore `EnsureCapacity`'s set minus `List<T>`.
// Extended again in the триста тридцать восьмой проход to also cover `PriorityQueue<TElement,
// TPriority>.EnsureCapacity(int capacity)` — found via the same reflection sweep used for the previous
// extension (checking every remaining `System.Collections.Generic` collection type for `EnsureCapacity`/
// `TrimExcess` methods), and confirmed empirically to throw identically to the rest of this family.
// `PriorityQueue<TElement, TPriority>.TrimExcess()` has no `int` overload (only the parameterless form,
// like `List<T>`), so it isn't added to `TrimExcessTargetTypes`.
// Extended again in the триста тридцать девятый проход to also cover `System.Text.StringBuilder.
// EnsureCapacity(int capacity)` — confirmed empirically to throw identically to the rest of this family.
// `StringBuilder` is NOT a generic type (unlike every other member of this family so far), so the
// containing-type check switched from `IsGenericType`/`ConstructedFrom` to `OriginalDefinition`, which
// works uniformly for both generic and non-generic containing types. `StringBuilder` has no
// `TrimExcess` method at all (it uses its settable `Length` property for that purpose instead, already
// covered by a completely different analyzer — `NegativeListCapacityAssignmentAnalyzer`/RPAD0293), so
// it's added only to `EnsureCapacityTargetTypes`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeEnsureCapacityArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0302";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative EnsureCapacity/TrimExcess argument always throws",
        messageFormat: "'{0}.{1}({2})' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "List<T>.EnsureCapacity, Dictionary<TKey,TValue>.EnsureCapacity/TrimExcess, HashSet<T>.EnsureCapacity/TrimExcess, Queue<T>.EnsureCapacity/TrimExcess, Stack<T>.EnsureCapacity/TrimExcess, PriorityQueue<TElement,TPriority>.EnsureCapacity, and StringBuilder.EnsureCapacity all throw ArgumentOutOfRangeException when capacity is negative.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> EnsureCapacityTargetTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.Queue<T>",
        "System.Collections.Generic.Stack<T>",
        "System.Collections.Generic.PriorityQueue<TElement, TPriority>",
        "System.Text.StringBuilder");

    private static readonly ImmutableHashSet<string> TrimExcessTargetTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.Queue<T>",
        "System.Collections.Generic.Stack<T>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "EnsureCapacity" or "TrimExcess" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var capacityArg }]) return;
        if (!TryGetNegativeIntValue(capacityArg, out var value)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        var targetTypes = methodName == "EnsureCapacity" ? EnsureCapacityTargetTypes : TrimExcessTargetTypes;
        if (method.ContainingType is not { } containingType
            || !targetTypes.Contains(containingType.OriginalDefinition.ToDisplayString()))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), access.Expression.ToString(), methodName, value));
    }

    private static bool TryGetNegativeIntValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
            })
        {
            return false;
        }
        if (literal.Token.Value is not int positiveValue) return false;

        value = -positiveValue;
        return true;
    }
}
