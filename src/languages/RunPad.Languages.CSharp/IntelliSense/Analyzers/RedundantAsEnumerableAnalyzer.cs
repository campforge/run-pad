using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят четвёртый проход, batch) — own pick, not a Roslynator port.
// `Enumerable.AsEnumerable()` exists to upcast a more specific sequence type (List<T>, an array, a
// query provider's IQueryable<T>...) to plain IEnumerable<T> — usually to pick LINQ-to-Objects over a
// more specific type's own same-named members, or to hide extension methods that would otherwise bind
// to the more specific interface. When the receiver's static type is ALREADY exactly IEnumerable<T>,
// none of that applies — the call can only return the same reference with the same static type, so it's
// a pure no-op. Scoped precisely to that case (via SpecialType.System_Collections_Generic_IEnumerable_T)
// rather than guessing from syntax, so a receiver typed as List<T>/IQueryable<T>/anything else is left
// alone.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantAsEnumerableAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0134";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant AsEnumerable() call",
        messageFormat: "'.AsEnumerable()' is a no-op here — the receiver is already statically typed as 'IEnumerable<T>'",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "AsEnumerable() upcasts a more specific sequence type to IEnumerable<T> — when the receiver is already exactly IEnumerable<T>, the call returns the same reference unchanged.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "AsEnumerable" } memberAccess)
            return;
        if (invocation.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "AsEnumerable" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Linq.Enumerable") return;

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;
        if (receiverType?.OriginalDefinition.SpecialType != SpecialType.System_Collections_Generic_IEnumerable_T)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
