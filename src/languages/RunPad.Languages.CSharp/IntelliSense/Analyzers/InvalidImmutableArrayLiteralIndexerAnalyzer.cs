using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as ArrayLiteralIndexerOutOfRangeAnalyzer (RPAD0283)/
// ListLiteralIndexerOutOfRangeAnalyzer (RPAD0303), applied to `System.Collections.Immutable.
// ImmutableArray<T>`: `ImmutableArray.Create(1, 2, 3)[10]`/`.RemoveAt(10)`/`.Insert(10, x)` all throw
// when the index is out of range for the literal's own known element count — confirmed empirically via
// scratch `dotnet run` script, including a genuinely surprising detail: the indexer throws
// `IndexOutOfRangeException` (ImmutableArray wraps a real array internally), but `.RemoveAt`/`.Insert`
// throw a DIFFERENT exception type, `ArgumentOutOfRangeException` — confirmed rather than assumed.
// `.Insert`'s upper boundary is `index <= count` (append at the end is valid), matching `List<T>.
// Insert`'s own established boundary (RPAD0303). The literal element count is read directly from the
// SYNTACTIC argument count of `ImmutableArray.Create(...)` or from `ImmutableArray<T>.Empty` (always
// 0). Two rounds of self-correction caught via PROBE, worth recording honestly: the FIRST version only
// accepted the fixed-arity `Create(T)`/`Create(T,T)`/`Create(T,T,T)`/`Create(T,T,T,T)` overloads
// (checking `createMethod.Parameters.Length == arguments.Count`), silently excluding sibling
// `ImmutableList.Create(...)` (RPAD0358) entirely, which has no such fixed-arity overload. The SECOND
// attempt added a `Create(params T[] items)` branch, assuming a params ARRAY overload — still wrong: a
// throwaway Roslyn symbol-resolution check showed `ImmutableList.Create(1, 2, 3)` (and, on closer
// inspection, `ImmutableArray.Create` too for the same call shape) actually resolves to `Create(params
// ReadOnlySpan<T> items)`, .NET's newer params-COLLECTIONS overload, not an array at all — so the
// array-typed check still matched nothing. Fixed by dropping the parameter-SHAPE reasoning entirely:
// `TryGetLiteralArgumentCount` now just reads the resolved generic method's own `TypeArguments[0]` (the
// element type `T`, however the overload is shaped) and verifies EVERY syntactic argument's own type is
// exactly `T` — true for `Create(literal, literal, ...)` regardless of which overload resolves, and
// correctly false for `Create(someArrayOrSpanVariable)` (whose single argument has type `T[]`/
// `ReadOnlySpan<T>`, not `T`).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidImmutableArrayLiteralIndexerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0357";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index is out of range for an ImmutableArray<T> literal",
        messageFormat: "'{0}' always throws {2} at runtime — this ImmutableArray<T> literal has only {1} element(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing, RemoveAt, or Insert on an ImmutableArray<T> literal (ImmutableArray.Create(...) or ImmutableArray<T>.Empty) with an index outside its own known element count throws IndexOutOfRangeException (indexer) or ArgumentOutOfRangeException (RemoveAt/Insert).");

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

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), total, "IndexOutOfRangeException"));
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
            || containingType.OriginalDefinition.ToDisplayString() != "System.Collections.Immutable.ImmutableArray<T>")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), total, "ArgumentOutOfRangeException"));
    }

    private static int? TryGetElementCount(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        if (expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Empty" } emptyAccess)
        {
            if (semanticModel.GetSymbolInfo(emptyAccess, ct).Symbol is IFieldSymbol { Name: "Empty", ContainingType: { } fieldType }
                && fieldType.OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableArray<T>")
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
            if (createMethod.ContainingType?.ToDisplayString() != "System.Collections.Immutable.ImmutableArray") return null;
            return TryGetLiteralArgumentCount(createMethod, arguments, semanticModel, ct);
        }

        return null;
    }

    private static int? TryGetLiteralArgumentCount(IMethodSymbol createMethod, SeparatedSyntaxList<ArgumentSyntax> arguments, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        // Regardless of which overload actually resolved (fixed-arity `Create(T, T, T)`, `Create(params
        // T[] items)`, or `Create(params ReadOnlySpan<T> items)` — .NET's newer params-collections
        // overloads, confirmed via a throwaway Roslyn symbol-resolution check to be what `ImmutableArray.
        // Create`/`ImmutableList.Create` actually resolve to for 1+ literal arguments, NOT a `T[]`
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
