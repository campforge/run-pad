using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "explicit .Default comparer is redundant" theme as RedundantDefaultComparerArgumentAnalyzer
// (RPAD0099), but for List&lt;T&gt;.Sort/BinarySearch and Array.Sort/BinarySearch instead
// of the LINQ extension methods RPAD0099 covers — those are instance/static methods on List&lt;T&gt;
// and Array, not System.Linq.Enumerable extensions, so they needed their own detection rather than an
// extra entry in RPAD0099's method-name set.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantDefaultComparerArgumentForSortAndBinarySearchAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0206";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant explicit default comparer argument",
        messageFormat: "Passing 'Comparer<T>.Default' explicitly is redundant — '{0}(...)' already uses it when no comparer is supplied",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Comparer<T>.Default is already what List<T>.Sort/BinarySearch and Array.Sort/BinarySearch use implicitly — passing it explicitly changes nothing.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethods = ImmutableHashSet.Create("Sort", "BinarySearch");

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
        if (!TargetMethods.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments.Count == 0) return;

        var lastArg = invocation.ArgumentList.Arguments[^1];
        if (lastArg.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Default",
                Expression: GenericNameSyntax { Identifier.Text: "Comparer" },
            })
        {
            return;
        }

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }

        var containingType = method.ContainingType;
        var isListOfT = containingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";
        var isArray = containingType?.ToDisplayString() == "System.Array";
        if (!isListOfT && !isArray) return;

        if (context.SemanticModel.GetSymbolInfo(lastArg.Expression, ct).Symbol is not IPropertySymbol { Name: "Default" } defaultProperty)
            return;
        if (defaultProperty.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.Comparer<T>")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
