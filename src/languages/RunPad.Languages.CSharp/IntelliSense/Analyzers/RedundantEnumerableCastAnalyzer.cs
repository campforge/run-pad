using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// RemoveRedundantCast-adjacent family, applied to LINQ's Cast<T>() (Apache License 2.0,
// https://github.com/dotnet/roslynator). `xs.Cast<int>()` where `xs` is already
// `IEnumerable<int>` does a no-op runtime type check on every element for nothing — same
// "identity conversion" idea as RedundantCastAnalyzer/RedundantAsOperatorAnalyzer (RPAD0022/RPAD0023), just for the
// LINQ method form of casting instead of a C-style cast/`as`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantEnumerableCastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0045";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Cast<T>() call",
        messageFormat: "This 'Cast<{0}>()' is redundant — the sequence is already IEnumerable<{0}>",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Cast<T>() checks every element's runtime type as it enumerates — a no-op cost when the sequence is already IEnumerable<T>.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.Text: "Cast", TypeArgumentList.Arguments: [var typeArgSyntax] },
            } memberAccess)
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var targetType = context.SemanticModel.GetTypeInfo(typeArgSyntax, ct).Type;
        if (targetType is null) return;

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;
        if (receiverType is null) return;

        var elementType = FindEnumerableElementType(receiverType);
        if (elementType is null || !SymbolEqualityComparer.Default.Equals(elementType, targetType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), targetType.ToDisplayString()));
    }

    private static ITypeSymbol? FindEnumerableElementType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } named)
            return named.TypeArguments[0];

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return iface.TypeArguments[0];
        }

        return null;
    }
}
