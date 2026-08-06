using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста сорок первый проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as ArrayLiteralIndexerOutOfRangeAnalyzer (RPAD0283, проход 300), applied
// to a freshly created `List<T>` collection-initializer instead of an array literal: `new List<int> { 1,
// 2, 3 }[10]`/`.RemoveAt(10)`/`.Insert(10, x)` all throw when the index is out of range for the
// initializer's own known element count — confirmed empirically (scratch `dotnet run` script, since
// deleted), and confirmed the exception type is `ArgumentOutOfRangeException` (List<T>'s indexer is a
// plain property, unlike Array's special-cased `IndexOutOfRangeException`). Unlike RPAD0283, this covers
// NEGATIVE indices too — `List<T>`'s indexer isn't subject to the compiler's CS0251 warning (that's
// specific to actual array-typed expressions), so there's no existing coverage to defer to. `Insert`
// has a genuinely different boundary than the indexer/`RemoveAt` — `index == count` is VALID for
// `Insert` (append at the end) but invalid for the other two (there's no element there to read/remove) —
// confirmed empirically, handled via a per-method boundary check. Scoped to the classic `new List<T> {
// ... }` object-creation-with-collection-initializer syntax only (not the `[1, 2, 3]` collection-
// expression form), matching RPAD0283's own scope precedent for this same reason.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ListLiteralIndexerOutOfRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0303";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Index is out of range for a List<T> literal",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — this List<T> literal has only {1} element(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Indexing, RemoveAt, or Insert on a freshly created List<T> collection initializer with an index outside its own known element count throws ArgumentOutOfRangeException.");

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
        if (!TryGetIntLiteralValue(indexArg, out var index)) return;

        var elementCount = TryGetListLiteralElementCount(elementAccess.Expression, context.SemanticModel, context.CancellationToken);
        if (elementCount is not { } count || index >= 0 && index < count) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), elementAccess.ToString(), count));
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
            if (invocation.ArgumentList.Arguments is not [{ Expression: var removeAtIndexArg }]) return;
            indexArg = removeAtIndexArg;
        }
        else
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var insertIndexArg }, _]) return;
            indexArg = insertIndexArg;
        }
        if (!TryGetIntLiteralValue(indexArg, out var index)) return;

        var elementCount = TryGetListLiteralElementCount(access.Expression, context.SemanticModel, context.CancellationToken);
        if (elementCount is not { } count) return;
        var upperBoundExclusive = methodName == "Insert" ? count + 1 : count;
        if (index >= 0 && index < upperBoundExclusive) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName }
            || resolvedName != methodName)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), count));
    }

    private static int? TryGetListLiteralElementCount(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        InitializerExpressionSyntax? initializer = expression switch
        {
            ObjectCreationExpressionSyntax { Initializer: { } explicitInit } => explicitInit,
            ImplicitObjectCreationExpressionSyntax { Initializer: { } implicitInit } => implicitInit,
            _ => null,
        };
        if (initializer is null || initializer.Kind() != SyntaxKind.CollectionInitializerExpression) return null;

        if (semanticModel.GetTypeInfo(expression, ct).Type is not { } type
            || type.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
        {
            return null;
        }

        return initializer.Expressions.Count;
    }

    private static bool TryGetIntLiteralValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } negLiteral,
            })
        {
            if (negLiteral.Token.Value is not int positive) return false;
            value = -positive;
            return true;
        }
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
            && literal.Token.Value is int positiveValue)
        {
            value = positiveValue;
            return true;
        }
        return false;
    }
}
