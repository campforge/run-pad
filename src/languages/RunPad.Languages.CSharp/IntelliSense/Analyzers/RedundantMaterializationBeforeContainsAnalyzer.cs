using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same LINQ-hygiene theme as
// RemoveRedundantMaterializationBeforeCountAnalyzer (RPAD0042), for Contains instead of
// Count: `xs.ToList().Contains(item)`/`xs.ToArray().Contains(item)`/`xs.ToHashSet().Contains(item)`
// materialize the ENTIRE sequence into a new collection just to check membership once — Contains(item)
// works directly on the original sequence via the same Enumerable.Contains extension, no copy needed.
// Deliberately requires the materialization call to be argument-less — `ToHashSet(comparer)` with an
// explicit comparer changes which equality check Contains would use, so that form is left alone.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantMaterializationBeforeContainsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0101";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant materialization before Contains",
        messageFormat: "'{0}().Contains(...)' materializes the whole sequence just to check membership — call 'Contains(...)' directly on the original sequence instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ToList()/ToArray()/ToHashSet() followed immediately by Contains allocates a throwaway copy of the whole sequence just to check membership once.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> MaterializationMethodNames = ImmutableHashSet.Create("ToList", "ToArray", "ToHashSet");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 1) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Contains" } memberAccess)
            return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } materializeInvocation)
            return;
        if (materializeInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var materializeName }
            || !MaterializationMethodNames.Contains(materializeName))
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(materializeInvocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.Name != materializeName) return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        var isRealMaterialization = original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable"
            || (materializeName == "ToArray" && original.ContainingType?.ToDisplayString() == "System.Collections.Generic.List<T>");
        if (!isRealMaterialization) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), materializeName));
    }
}
