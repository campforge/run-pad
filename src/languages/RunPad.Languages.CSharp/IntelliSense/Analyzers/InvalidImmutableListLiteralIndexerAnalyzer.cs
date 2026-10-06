using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят пятый проход, batch) — own pick, not a Roslynator port. Sibling of
// InvalidImmutableArrayLiteralIndexerAnalyzer (RPAD0357, same проход) — same theme, applied to
// `System.Collections.Immutable.ImmutableList<T>` instead: `ImmutableList.Create(1, 2, 3)[10]`/
// `.RemoveAt(10)`/`.Insert(10, x)` all throw when the index is out of range — confirmed empirically via
// scratch `dotnet run` script. Kept as a SEPARATE analyzer from RPAD0357 rather than folded in, because
// the exception-type split genuinely differs: `ImmutableList<T>`'s INDEXER throws
// `ArgumentOutOfRangeException` (unlike `ImmutableArray<T>`'s indexer, which throws
// `IndexOutOfRangeException`, confirmed in RPAD0357), so here the indexer and `RemoveAt`/`Insert` all
// share the SAME exception type — a genuinely different mechanism shape worth its own analyzer, matching
// this project's established precedent for exception-type-driven separation. Element-count extraction
// (`TryGetLiteralArgumentCount`) reads the resolved `Create` method's own generic `TypeArguments[0]`
// rather than reasoning about the parameter shape — see RPAD0357's own comment for the two rounds of
// self-correction that led here: `ImmutableList.Create` has no fixed-arity overload beyond zero
// arguments, and its params overload turned out to take `ReadOnlySpan<T>`, not `T[]` as first assumed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidImmutableListLiteralIndexerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0358";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index is out of range for an ImmutableList<T> literal",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — this ImmutableList<T> literal has only {1} element(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing, RemoveAt, or Insert on an ImmutableList<T> literal (ImmutableList.Create(...) or ImmutableList<T>.Empty) with an index outside its own known element count throws ArgumentOutOfRangeException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeElementAccess, SyntaxKind.ElementAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeElementAccess(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;

        var count = TryGetElementCount(elementAccess.Expression, context.SemanticModel, ct);
        if (count is not { } total || (index >= 0 && index < total)) return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { ContainingType: { } propertyType }
            || propertyType.OriginalDefinition.ToDisplayString() != "System.Collections.Immutable.ImmutableList<T>")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), total));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "RemoveAt" or "Insert" } access)
            return;
        var methodName = access.Name.Identifier.Text;

        ExpressionSyntax indexArg;
        if (methodName == "RemoveAt")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var removeAtArg }]) return;
            indexArg = removeAtArg;
        }
        else
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var insertArg }, _]) return;
            indexArg = insertArg;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;

        var count = TryGetElementCount(access.Expression, context.SemanticModel, ct);
        if (count is not { } total) return;
        var upperBoundExclusive = methodName == "Insert" ? total + 1 : total;
        if (index >= 0 && index < upperBoundExclusive) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName, ContainingType: { } containingType }
            || resolvedName != methodName
            || containingType.OriginalDefinition.ToDisplayString() != "System.Collections.Immutable.ImmutableList<T>")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), total));
    }

    private static int? TryGetElementCount(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        if (expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Empty" } emptyAccess)
        {
            if (semanticModel.GetSymbolInfo(emptyAccess, ct).Symbol is IFieldSymbol { Name: "Empty", ContainingType: { } fieldType }
                && fieldType.OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableList<T>")
            {
                return 0;
            }
            return null;
        }

        if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Create" } } createInvocation)
        {
            var arguments = createInvocation.ArgumentList.Arguments;
            if (semanticModel.GetSymbolInfo(createInvocation, ct).Symbol is not IMethodSymbol { Name: "Create" } createMethod)
                return null;
            if (createMethod.ContainingType?.ToDisplayString() != "System.Collections.Immutable.ImmutableList") return null;
            return TryGetLiteralArgumentCount(createMethod, arguments, semanticModel, ct);
        }

        return null;
    }

    private static int? TryGetLiteralArgumentCount(IMethodSymbol createMethod, SeparatedSyntaxList<ArgumentSyntax> arguments, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        // Regardless of which overload actually resolved (fixed-arity `Create(T, T, T)`, `Create(params
        // T[] items)`, or `Create(params ReadOnlySpan<T> items)` — .NET's newer params-collections
        // overloads, confirmed via a throwaway Roslyn symbol-resolution check to be what `ImmutableList.
        // Create`/`ImmutableArray.Create` actually resolve to for 1+ literal arguments, NOT a `T[]`
        // overload as first assumed), the syntactic argument count equals the element count as long as
        // EVERY individual argument's own type is exactly the element type `T` — i.e. `Create(literal,
        // literal, ...)`, not `Create(someArrayOrSpanVariable)` (whose single argument would have type
        // `T[]`/`ReadOnlySpan<T>`, not `T`, and correctly fails this check).
        if (createMethod.TypeArguments is not [{ } elementType]) return null;
        foreach (var argument in arguments)
        {
            if (!SymbolEqualityComparer.Default.Equals(semanticModel.GetTypeInfo(argument.Expression, ct).Type, elementType))
                return null;
        }
        return arguments.Count;
    }
}
