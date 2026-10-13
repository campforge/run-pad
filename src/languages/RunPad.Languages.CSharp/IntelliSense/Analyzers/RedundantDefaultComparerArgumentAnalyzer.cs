using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Contains(item, EqualityComparer<T>.Default)`/`xs.OrderBy(key, Comparer<T>.Default)` spell out
// exactly the comparer these methods already use when none is supplied — passing `.Default` explicitly
// changes nothing, it's just extra text. Scoped to LINQ methods where this is unambiguous
// (Contains/Distinct/SequenceEqual/Except/Intersect/Union/GroupBy/ToDictionary/ToHashSet for
// IEqualityComparer, OrderBy/OrderByDescending/ThenBy/ThenByDescending/MinBy/MaxBy for IComparer)
// rather than every overload that happens to accept a comparer.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantDefaultComparerArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0099";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant explicit default comparer argument",
        messageFormat: "Passing '{0}<T>.Default' explicitly is redundant — '{1}(...)' already uses it when no comparer is supplied",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "EqualityComparer<T>.Default/Comparer<T>.Default is already what these LINQ methods use implicitly — passing it explicitly changes nothing.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> EqualityComparerMethods = ImmutableHashSet.Create(
        "Contains", "Distinct", "SequenceEqual", "Except", "Intersect", "Union", "GroupBy", "ToDictionary", "ToHashSet");
    private static readonly ImmutableHashSet<string> ComparerMethods = ImmutableHashSet.Create(
        "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "MinBy", "MaxBy");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (invocation.ArgumentList.Arguments.Count == 0) return;

        var expectedComparerTypeName = EqualityComparerMethods.Contains(methodName) ? "EqualityComparer"
            : ComparerMethods.Contains(methodName) ? "Comparer"
            : null;
        if (expectedComparerTypeName is null) return;

        var lastArg = invocation.ArgumentList.Arguments[^1];
        if (lastArg.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Default",
                Expression: GenericNameSyntax { Identifier.Text: var comparerTypeName },
            })
        {
            return;
        }
        if (comparerTypeName != expectedComparerTypeName) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (context.SemanticModel.GetSymbolInfo(lastArg.Expression, ct).Symbol is not IPropertySymbol { Name: "Default" } defaultProperty)
            return;
        var expectedContainingType = $"System.Collections.Generic.{expectedComparerTypeName}<T>";
        if (defaultProperty.ContainingType?.OriginalDefinition.ToDisplayString() != expectedContainingType) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), expectedComparerTypeName, methodName));
    }
}
