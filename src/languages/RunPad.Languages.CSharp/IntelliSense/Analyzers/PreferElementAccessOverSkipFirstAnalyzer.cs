using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят первый проход, batch) — same "prefer direct indexing" theme as
// UseElementAccessInsteadOfLinqMethodAnalyzer (RPAD0016, проход 147), for a chain instead of a single
// call: `xs.Skip(n).First()` walks an enumerator past the first n elements and then reads the next one
// — `xs[n]` reads the same element directly when the receiver already supports indexing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferElementAccessOverSkipFirstAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0121";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use element access instead of Skip(n).First()",
        messageFormat: "Use '[{0}]' instead of 'Skip({0}).First()' — the receiver already supports direct indexing",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Skip(n).First() walks an enumerator past n elements to read the next one; indexing directly is just as clear when the receiver already supports it.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "First" } memberAccess)
            return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments: [var skipArgument] } skipInvocation)
            return;
        if (skipInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Skip" } skipAccess) return;
        if (!IsRealEnumerableMethod(context, skipInvocation, ct)) return;

        var receiverType = context.SemanticModel.GetTypeInfo(skipAccess.Expression, ct).Type;
        if (receiverType is null || !HasInt32Indexer(receiverType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), skipArgument.Expression.ToString()));
    }

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }

    private static bool HasInt32Indexer(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol) return true;

        foreach (var candidate in EnumerateSelfAndInterfaces(type))
        {
            foreach (var member in candidate.GetMembers())
            {
                if (member is IPropertySymbol { IsIndexer: true } indexer
                    && indexer.Parameters is [{ Type.SpecialType: SpecialType.System_Int32 }])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<ITypeSymbol> EnumerateSelfAndInterfaces(ITypeSymbol type)
    {
        yield return type;
        foreach (var iface in type.AllInterfaces) yield return iface;
    }
}
