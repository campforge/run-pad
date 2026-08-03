using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести пятый проход, batch) — own pick, not a Roslynator port.
// `new Random().Next(...)` constructs a Random instance just to call one method on it and immediately
// discard it — a distinct idea from RandomInstantiatedInLoopAnalyzer (RPAD0133, проход 169), which only
// fires when the construction sits inside a loop. This one fires regardless of loop context: even a
// single throwaway Random() is wasteful (thread-static seeding overhead for an object used once) and,
// since .NET 6, Random.Shared is a ready-made thread-safe singleton built for exactly this call shape.
// Restricted to the parameterless constructor only — an explicit seed argument signals the caller
// wants reproducible output, which Random.Shared cannot provide (it can't be seeded).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferRandomSharedOverThrowawayRandomInstanceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0193";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Random.Shared over a throwaway new Random() instance",
        messageFormat: "'new Random().{0}(...)' constructs an instance just to call it once and discard it — 'Random.Shared.{0}(...)' does the same without the throwaway allocation",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "new Random().Next(...) (or any other Random method) chained directly on the constructor allocates an instance that is used exactly once — Random.Shared is a thread-safe singleton built for this call shape.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName, Expression: ObjectCreationExpressionSyntax creation })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol constructor)
            return;
        if (constructor.ContainingType?.ToDisplayString() != "System.Random") return;
        // An explicit seed means the caller wants reproducible output — Random.Shared can't be seeded,
        // so only the parameterless constructor (non-reproducible by construction already) is safe to flag.
        if (constructor.Parameters.Length != 0) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Random") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
