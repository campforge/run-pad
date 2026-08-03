using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести двадцать шестой проход, batch) — own pick, not a Roslynator port.
// `a.GetHashCode() ^ b.GetHashCode()` is the old hand-rolled way to combine two hash codes into one —
// but XOR is commutative and self-cancelling, so it distributes poorly: swapping two equal-typed
// fields (or two equal values) produces the same combined hash, and combining a value with itself
// always yields zero. `HashCode.Combine(a, b)` (.NET Core 2.1+, within this project's .NET 6 SDK
// floor) mixes the inputs properly and is what GetHashCode overrides are meant to use today.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferHashCodeCombineOverXorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0209";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer HashCode.Combine over XOR-ing GetHashCode() results",
        messageFormat: "'a.GetHashCode() ^ b.GetHashCode()' distributes poorly (XOR is commutative and self-cancelling) — 'HashCode.Combine(a, b)' mixes the hash codes properly",
        category: "Reliability",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "XOR-ing two GetHashCode() results is a known-weak way to combine hashes (order doesn't matter, and combining equal values yields zero) — HashCode.Combine mixes its inputs properly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ExclusiveOrExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        if (!IsGetHashCodeCall(binary.Left)) return;
        if (!IsGetHashCodeCall(binary.Right)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(binary.Left, ct).Symbol is not IMethodSymbol { Name: "GetHashCode" })
            return;
        if (context.SemanticModel.GetSymbolInfo(binary.Right, ct).Symbol is not IMethodSymbol { Name: "GetHashCode" })
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsGetHashCodeCall(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetHashCode" },
            ArgumentList.Arguments.Count: 0,
        };
}
