using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят шестой проход, batch) — own pick, not a Roslynator port. `ImmutableArray
// <T>` is a STRUCT wrapping an internal array field — `default(ImmutableArray<T>)` is therefore a
// non-null struct VALUE whose internal array field is null (`IsDefault == true`), and touching almost any
// member that reads through that field throws `NullReferenceException` — a genuinely surprising failure
// mode for a value type, confirmed empirically via scratch `dotnet run` script for the indexer, `.Length`,
// `.IsEmpty`, `.RemoveAt`, `.Insert`, `.Add`, `.Contains`, and `.IndexOf` (all throw identically), while
// `.IsDefault`/`.IsDefaultOrEmpty` do NOT throw (confirmed too — those are the two members specifically
// designed to be safe to call on a default instance, so they're deliberately excluded here). Distinct
// from InvalidImmutableArrayLiteralIndexerAnalyzer (RPAD0357, проход 365) both in exception type
// (NullReferenceException here vs. IndexOutOfRangeException/ArgumentOutOfRangeException there) and in
// receiver shape (`default(ImmutableArray<T>)` — an uninitialized struct — vs. `ImmutableArray.Create
// (...)`/`ImmutableArray<T>.Empty` — a genuinely empty-but-initialized one). Scoped, like the rest of this
// catalog, to a DIRECT chain off the `default(ImmutableArray<T>)` expression itself (confirmed this
// compiles and throws identically to the two-statement `ImmutableArray<int> a = default; a[0];` form) —
// not a variable that was assigned `default` earlier, which would need real data-flow analysis.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DefaultImmutableArrayMemberAccessAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0362";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Member access on a default(ImmutableArray<T>) always throws",
        messageFormat: "'default(ImmutableArray<...>){0}' always throws NullReferenceException at runtime — a default/uninitialized ImmutableArray<T> wraps a null backing array",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ImmutableArray<T> is a struct wrapping an internal array field; default(ImmutableArray<T>) leaves that field null, so its indexer, Length, IsEmpty, RemoveAt, Insert, Add, Contains, and IndexOf all throw NullReferenceException (unlike IsDefault/IsDefaultOrEmpty, which are safe to call).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeElementAccess, SyntaxKind.ElementAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeElementAccess(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        var ct = context.CancellationToken;
        if (!IsDefaultImmutableArrayExpression(elementAccess.Expression, context.SemanticModel, ct)) return;

        if (context.SemanticModel.GetSymbolInfo(elementAccess, ct).Symbol is not IPropertySymbol { IsIndexer: true, ContainingType: { } indexerType }
            || indexerType.OriginalDefinition.Name != "ImmutableArray")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation(), "[...]"));
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var access = (MemberAccessExpressionSyntax)context.Node;
        if (access.Name.Identifier.Text is not ("Length" or "IsEmpty")) return;
        var memberName = access.Name.Identifier.Text;
        // Only a plain property read, not the receiver of an invocation this analyzer would double-fire
        // on — moot here since Length/IsEmpty are properties, never invoked, but kept for clarity.
        if (access.Parent is InvocationExpressionSyntax { Expression: var invoked } && invoked == access) return;

        var ct = context.CancellationToken;
        if (!IsDefaultImmutableArrayExpression(access.Expression, context.SemanticModel, ct)) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { ContainingType: { } propertyType }
            || propertyType.OriginalDefinition.Name != "ImmutableArray")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, access.GetLocation(), "." + memberName));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "RemoveAt" or "Insert" or "Add" or "Contains" or "IndexOf" } access)
            return;
        var methodName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (!IsDefaultImmutableArrayExpression(access.Expression, context.SemanticModel, ct)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName, ContainingType: { } containingType }
            || resolvedName != methodName
            || containingType.OriginalDefinition.Name != "ImmutableArray")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "." + methodName + "(...)"));
    }

    private static bool IsDefaultImmutableArrayExpression(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken ct)
    {
        if (expression is not DefaultExpressionSyntax defaultExpr) return false;
        var type = semanticModel.GetTypeInfo(defaultExpr, ct).Type;
        return type?.OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableArray<T>";
    }
}
