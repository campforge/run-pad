using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A performance
// finding, not a correctness one: `new List<T>(capacity) { e1, e2, ..., en }` (same for HashSet<T>/
// Dictionary<TKey,TValue>) passes an explicit capacity specifically to avoid the internal array being
// resized as elements are added — but if that literal capacity is smaller than the number of elements
// actually listed in the initializer, at least one resize still happens during construction, defeating
// the entire point of specifying a capacity. Scoped to List<T>/HashSet<T>/Dictionary<TKey,TValue> —
// the only capacity-taking types in RPAD0231's family that also support the `{ ... }` collection-
// initializer syntax (Queue<T>/Stack<T>/StringBuilder/PriorityQueue don't have a public `Add` matching
// that shape). Counts the top-level initializer elements structurally (works the same for a plain value
// list or `{ key, value }` pairs) rather than resolving each element's own Add overload.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InsufficientCollectionCapacityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0243";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Initializer has more elements than the given capacity",
        messageFormat: "Capacity {0} is less than the {1} element(s) in this initializer — at least one internal resize still happens, defeating the point of specifying a capacity",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Specifying a capacity smaller than the initializer's own element count still triggers at least one internal array resize during construction.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetTypes = ImmutableHashSet.Create(
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.Dictionary<TKey, TValue>");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.Initializer is not { RawKind: (int)SyntaxKind.CollectionInitializerExpression } initializer)
            return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var capacityArg }, ..] }) return;
        if (capacityArg is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } capacityLiteral)
            return;
        if (capacityLiteral.Token.Value is not int capacity) return;

        var elementCount = initializer.Expressions.Count;
        if (capacity >= elementCount) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.Parameters is not [{ Name: "capacity", Type.SpecialType: SpecialType.System_Int32 }, ..]) return;
        if (ctor.ContainingType is not { } containingType) return;
        if (!TargetTypes.Contains(containingType.OriginalDefinition.ToDisplayString())) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), capacity, elementCount));
    }
}
