using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `new Regex(pattern).IsMatch(input)` (same for Match/Matches/Replace/Split) constructs a throwaway
// Regex instance just to make one call and discard it. The static `Regex.IsMatch(input, pattern)`
// family does the same match — and, unlike the throwaway instance, benefits from Regex's own internal
// cache of compiled patterns (up to 15 by default), so a repeated call with the same pattern string
// doesn't recompile it. Scoped to the single-argument Regex(pattern) constructor — the
// Regex(pattern, options) overload would need the static call to carry the options through too, which
// this first pass doesn't attempt.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferStaticRegexMethodOverNewRegexInstanceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0161";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the static Regex method over a throwaway Regex instance",
        messageFormat: "'new Regex(pattern).{0}(...)' constructs a throwaway Regex for one call — use the static 'Regex.{0}(...)' overload, which also benefits from Regex's own pattern cache",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A Regex constructed only to make one call and discard it wastes the construction and misses Regex's own cache of compiled patterns that the static methods use.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethodNames = ImmutableHashSet.Create(
        "IsMatch", "Match", "Matches", "Replace", "Split");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } methodAccess)
            return;
        if (!TargetMethodNames.Contains(methodName)) return;

        if (methodAccess.Expression is not ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 1 } objectCreation)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(objectCreation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            return;
        if (constructor.ContainingType?.ToDisplayString() != "System.Text.RegularExpressions.Regex") return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: false } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Text.RegularExpressions.Regex") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
