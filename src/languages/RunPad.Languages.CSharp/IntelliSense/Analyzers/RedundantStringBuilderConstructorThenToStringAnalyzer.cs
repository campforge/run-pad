using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `new StringBuilder(s).ToString()` constructs a whole StringBuilder around `s` just to immediately
// unwrap it again — with nothing appended in between, ToString() returns exactly `s` back, so the
// StringBuilder never did anything observable. Scoped to the single-string constructor overload
// (StringBuilder(string)) chained directly into ToString() with no intervening calls — the only shape
// where this is provably a no-op.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantStringBuilderConstructorThenToStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0151";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant StringBuilder(s).ToString() round trip",
        messageFormat: "'new StringBuilder(s).ToString()' wraps and immediately unwraps 's' with nothing appended in between — use 's' directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "new StringBuilder(s).ToString(), with no Append calls in between, returns exactly s — the StringBuilder never did anything observable.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" } toStringAccess)
            return;
        if (invocation.ArgumentList.Arguments.Count != 0) return;

        if (toStringAccess.Expression is not ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 1 } objectCreation)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(objectCreation, ct).Symbol is not IMethodSymbol
            { MethodKind: MethodKind.Constructor, Parameters: [{ Type.SpecialType: SpecialType.System_String }] } constructor)
        {
            return;
        }
        if (constructor.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
