using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// UseElementAccessInsteadOfFirst/ElementAt analyzers (Apache License 2.0,
// https://github.com/dotnet/roslynator), merged into one class since both flag the identical shape
// (a LINQ Enumerable method that could be a plain indexer access on a type that already has one).
// `list.First()`/`list.ElementAt(n)` walk an IEnumerator to reach the answer; `list[0]`/`list[n]` is
// direct and just as clear when the receiver is already indexable.
// Extended to also cover `Last()` → `[^1]`, using the C# 8+
// index-from-end operator — requires the receiver to also have a Count/Length property (that's what
// makes `^1` resolvable at all; a bare int indexer alone isn't enough), unlike First()/ElementAt(n)
// which only ever need the indexer itself.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseElementAccessInsteadOfLinqMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0016";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use element access instead of a LINQ method",
        messageFormat: "Use '[{0}]' instead of '{1}()' — the receiver already supports direct indexing",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "First()/ElementAt(n) walk an enumerator; indexing directly is just as clear when the receiver already supports it.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "First" or "ElementAt" or "Last" } memberAccess)
            return;

        var methodName = memberAccess.Name.Identifier.Text;
        var arguments = invocation.ArgumentList.Arguments;
        string indexText;
        if (methodName == "First" && arguments.Count == 0) indexText = "0";
        else if (methodName == "ElementAt" && arguments.Count == 1) indexText = arguments[0].Expression.ToString();
        else if (methodName == "Last" && arguments.Count == 0) indexText = "^1";
        else return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        if (receiverType is null) return;
        if (!HasInt32Indexer(receiverType)) return;
        // `^1` additionally needs a Count/Length property to resolve — First()/ElementAt(n) don't.
        if (methodName == "Last" && !HasCountOrLengthProperty(receiverType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), indexText, methodName));
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

    private static bool HasCountOrLengthProperty(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol) return true;

        foreach (var candidate in EnumerateSelfAndInterfaces(type))
        {
            foreach (var member in candidate.GetMembers())
            {
                if (member is IPropertySymbol { Name: "Count" or "Length", Type.SpecialType: SpecialType.System_Int32 })
                    return true;
            }
        }

        return false;
    }
}
