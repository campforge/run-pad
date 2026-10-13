using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. `new
// Random()` inside a loop body allocates a fresh generator (and discards its internal state) on every
// iteration instead of reusing one generator across the whole loop — pure waste, and historically also
// a correctness trap: Random's parameterless constructor seeds from the system clock, so two instances
// created close together (a tight loop can do this many times within the clock's resolution) can start
// from the same seed and produce correlated sequences. Hoisting the instance outside the loop (or using
// the shared `Random.Shared`, available since .NET 6 — this project's SDK floor) avoids both problems.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RandomInstantiatedInLoopAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0133";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Random instantiated inside a loop",
        messageFormat: "'new Random()' inside a loop creates a new generator every iteration — hoist it outside the loop (or use 'Random.Shared') so one generator is reused",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Creating a new Random instance every loop iteration wastes the work of seeding it and, for instances created close together, risks correlated sequences from clock-resolution seed collisions.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var objectCreation = (ObjectCreationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(objectCreation, context.CancellationToken).Symbol is not IMethodSymbol
            { MethodKind: MethodKind.Constructor } constructor)
        {
            return;
        }
        if (constructor.ContainingType?.ToDisplayString() != "System.Random") return;

        if (!IsInsideLoopThatRunsEachIteration(objectCreation)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, objectCreation.GetLocation()));
    }

    private static bool IsInsideLoopThatRunsEachIteration(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case ForStatementSyntax forStatement:
                    var isInOneTimeInit = (forStatement.Declaration is { } decl && decl.Span.Contains(node.Span))
                        || forStatement.Initializers.Any(init => init.Span.Contains(node.Span));
                    return !isInOneTimeInit;
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForEachStatementSyntax:
                    return true;
                case MethodDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case SimpleLambdaExpressionSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                    return false;
            }
        }

        return false;
    }
}
