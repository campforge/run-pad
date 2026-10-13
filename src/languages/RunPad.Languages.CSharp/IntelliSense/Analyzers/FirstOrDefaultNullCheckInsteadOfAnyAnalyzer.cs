using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same LINQ-hygiene theme as the passes 155/156
// batch. `xs.FirstOrDefault(pred) != null` finds and returns the whole matching element just to
// immediately throw it away and keep only whether one existed — `xs.Any(pred)` says exactly that,
// directly, and reads the same either way regardless of what the found element's default value for T
// happens to be.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FirstOrDefaultNullCheckInsteadOfAnyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0058";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Any() instead of a FirstOrDefault() null check",
        messageFormat: "Use 'Any(...)' instead of comparing 'FirstOrDefault(...)' to null when only checking existence",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "FirstOrDefault(pred) != null finds and discards the matching element just to check whether one exists — Any(pred) says that directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var candidate = binary.Right.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Left
            : binary.Left.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Right : null;
        if (candidate is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } invocation) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FirstOrDefault" }) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }
}
