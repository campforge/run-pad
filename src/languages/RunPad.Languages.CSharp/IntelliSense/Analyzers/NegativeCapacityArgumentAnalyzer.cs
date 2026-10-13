using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A correctness
// finding: `List<T>`/`Dictionary<TKey,TValue>`/`HashSet<T>`/`Queue<T>`/`Stack<T>`/`StringBuilder` all
// have a constructor overload accepting an initial capacity, and every one of them throws
// `ArgumentOutOfRangeException` at runtime if that capacity is negative — a mistake the compiler can't
// catch on its own (the constructor argument is a perfectly normal int). Scoped to a LITERAL negative
// integer only (not an arbitrary negative-valued expression) — proving an arbitrary expression is always
// negative would need real data-flow analysis, which is out of scope for this kind of syntactic
// analyzer; a literal is unambiguous. `new T[-1]` (a negative ARRAY size) was considered alongside this
// during PROBE and dropped — the compiler's own diagnostics already reject a constant-folded negative
// array size, so there was nothing left for this analyzer to add there.
// Extended to also cover `PriorityQueue<TElement,TPriority>` — same
// negative-capacity constructor throw, just a newer collection type (.NET 6+, within this project's own
// SDK floor). Its constructor parameter is named `initialCapacity` rather than `capacity`, so the
// parameter-name check now accepts either spelling.
// Extended again to also cover `SortedList<TKey, TValue>(int
// capacity)` — confirmed empirically (both the throw and, via reflection, the exact constructor parameter
// name `"capacity"`, matching the existing check unchanged) — simply added to `TargetTypes`.
// Extended again to also cover `System.IO.MemoryStream(int
// capacity)` — confirmed via reflection (not assumed) that its constructor parameter is also literally
// named `"capacity"`, and confirmed empirically it throws identically — simply added to `TargetTypes`.
// Extended again to
// also cover `System.Collections.Specialized.NameValueCollection(int capacity)` — confirmed via
// reflection its constructor parameter is also literally named `"capacity"`, and confirmed empirically
// it throws identically — simply added to `TargetTypes`. `HybridDictionary(int)` was checked alongside
// this and does NOT throw for a negative value (declined); `StringDictionary` has no capacity-taking
// constructor overload at all (declined).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeCapacityArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0231";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative capacity always throws ArgumentOutOfRangeException",
        messageFormat: "'new {0}({1})' always throws ArgumentOutOfRangeException at runtime — capacity cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "List<T>, Dictionary<TKey,TValue>, HashSet<T>, Queue<T>, Stack<T>, PriorityQueue<TElement,TPriority>, SortedList<TKey,TValue>, StringBuilder, MemoryStream, and NameValueCollection all throw ArgumentOutOfRangeException when constructed with a negative capacity.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.Queue<T>",
        "System.Collections.Generic.Stack<T>",
        "System.Collections.Generic.PriorityQueue<TElement, TPriority>",
        "System.Collections.Generic.SortedList<TKey, TValue>",
        "System.Text.StringBuilder",
        "System.IO.MemoryStream",
        "System.Collections.Specialized.NameValueCollection");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var firstArg }, ..] }) return;

        if (!TryGetNegativeLiteralValue(firstArg, out var literalText)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.Parameters is not [{ Name: "capacity" or "initialCapacity", Type.SpecialType: SpecialType.System_Int32 }, ..])
            return;
        if (ctor.ContainingType is not { } containingType) return;
        if (!TargetTypes.Contains(containingType.OriginalDefinition.ToDisplayString())) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), containingType.Name, literalText));
    }

    private static bool TryGetNegativeLiteralValue(ExpressionSyntax expression, out string literalText)
    {
        literalText = "";
        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
            })
        {
            return false;
        }

        literalText = "-" + literal.Token.ValueText;
        return true;
    }
}
