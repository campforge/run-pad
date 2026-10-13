using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same LINQ-hygiene theme as UseCountOrLengthPropertyInsteadOfAnyMethodAnalyzer/
// CallAnyInsteadOfCountAnalyzer (RPAD0015/RPAD0017, pass 147): `xs.ToList().Count`/`xs.ToArray().Length`
// materializes the ENTIRE sequence into a new list/array just to read its count — `xs.Count()`
// (LINQ's own Enumerable.Count(), which the earlier CallAnyInsteadOfCountAnalyzer already knows to
// prefer Any() over when comparing to zero, but is still the right call here) gets the same number
// without the throwaway allocation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantMaterializationBeforeCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0042";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant materialization before counting",
        messageFormat: "'{0}().{1}' materializes the whole sequence just to count it — use 'Count()' directly on the original sequence instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ToList()/ToArray() followed immediately by Count/Length allocates a throwaway copy of the whole sequence just to read its size.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        var propertyName = memberAccess.Name.Identifier.Text;

        var expectedSourceMethod = propertyName switch
        {
            "Count" => "ToList",
            "Length" => "ToArray",
            _ => null,
        };
        if (expectedSourceMethod is null) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } invocation)
            return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var calledMethod })
            return;
        if (calledMethod != expectedSourceMethod) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.GetLocation(), calledMethod, propertyName));
    }
}
