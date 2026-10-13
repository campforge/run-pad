using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `x.Trim().Trim()` (same for TrimStart/TrimEnd) calls the same idempotent, argument-less trim method
// twice in a row — the first call already removes all the leading/trailing whitespace the second call
// would look for, so the second call always has nothing left to do. Scoped to the parameterless
// overloads specifically (both calls with zero arguments) — a char[] trim-set argument could differ
// between the two calls, and this analyzer isn't trying to prove that case redundant too.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantConsecutiveTrimCallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0158";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant consecutive Trim call",
        messageFormat: "'.{0}().{0}()' calls the same trim method twice — the first call already removes everything the second one would look for",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Trim/TrimStart/TrimEnd are idempotent when called with no arguments — calling the same one twice in a row leaves the second call with nothing to do.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TrimMethodNames = ImmutableHashSet.Create("Trim", "TrimStart", "TrimEnd");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var outerName } outerAccess)
            return;
        if (!TrimMethodNames.Contains(outerName)) return;

        if (outerAccess.Expression is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var innerName },
            } innerInvocation)
        {
            return;
        }
        if (innerName != outerName) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol outerMethod
            || outerMethod.ContainingType?.SpecialType != SpecialType.System_String)
        {
            return;
        }
        if (context.SemanticModel.GetSymbolInfo(innerInvocation, ct).Symbol is not IMethodSymbol innerMethod
            || innerMethod.ContainingType?.SpecialType != SpecialType.System_String)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), outerName));
    }
}
