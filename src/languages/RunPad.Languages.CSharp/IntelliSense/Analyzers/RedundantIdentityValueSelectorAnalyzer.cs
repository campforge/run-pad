using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто девяносто первый проход, batch) — own pick, not a Roslynator port.
// `xs.ToDictionary(key, x => x)`/`xs.ToLookup(key, x => x)` spells out an identity value selector —
// but the single-selector overloads of both methods already use the element itself as the value when
// no value selector is given. Matched structurally (the value-selector lambda's body must be exactly
// its own parameter, checked via the identifier text) — not just "looks like a pass-through".
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantIdentityValueSelectorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0179";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant identity value selector",
        messageFormat: "The value selector just returns its own element — the single-selector '{0}' overload already does that when no value selector is given",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ToDictionary(key, x => x)/ToLookup(key, x => x) spell out an identity value selector that the single-selector overload of each method already applies by default.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethodNames = ImmutableHashSet.Create("ToDictionary", "ToLookup");

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
        if (!TargetMethodNames.Contains(methodName)) return;

        if (invocation.ArgumentList.Arguments is not [_, { Expression: SimpleLambdaExpressionSyntax { Parameter: var param, Body: IdentifierNameSyntax bodyId } }])
            return;
        if (bodyId.Identifier.Text != param.Identifier.Text) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
