using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same LINQ-hygiene theme as the passes 155/156
// batch. `xs.Concat(new[] { item })` allocates a throwaway one-element array just to concatenate a
// single value onto a sequence — `xs.Append(item)` (and, symmetrically, `new[] { item }.Concat(xs)`
// → `xs.Prepend(item)`) does the same thing without the array.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseAppendPrependInsteadOfConcatWithArrayAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0057";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Append/Prepend instead of Concat with a single-element array",
        messageFormat: "Use '{0}(...)' instead of 'Concat' with a single-element array — avoids allocating a throwaway array",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Concat with a single-element array literal allocates a throwaway array just to add one value — Append/Prepend do the same without it.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Concat" } memberAccess)
            return;
        if (invocation.ArgumentList.Arguments is not [var soleArg]) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        if (original.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        if (IsSingleElementArrayLiteral(soleArg.Expression))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Append"));
        }
        else if (IsSingleElementArrayLiteral(memberAccess.Expression))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "Prepend"));
        }
    }

    private static bool IsSingleElementArrayLiteral(ExpressionSyntax expression) => expression switch
    {
        ArrayCreationExpressionSyntax { Initializer.Expressions.Count: 1 } => true,
        ImplicitArrayCreationExpressionSyntax { Initializer.Expressions.Count: 1 } => true,
        _ => false,
    };
}
