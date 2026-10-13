using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "spelling out exactly what the default parameter already provides" theme as
// RedundantDefaultComparerArgumentAnalyzer (RPAD0099, проход 167), for a different, very common
// parameter shape: passing `CancellationToken.None` explicitly to an optional `CancellationToken`
// parameter whose own default is `default` — omitting the argument entirely already gets exactly that
// same value (default(CancellationToken) and CancellationToken.None are the same value; None is just
// the named, documented way to write it). Matched positionally only (named/reordered arguments are
// deliberately left alone, since matching those correctly needs more care than this first pass takes).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantCancellationTokenNoneArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0182";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant explicit CancellationToken.None argument",
        messageFormat: "Passing 'CancellationToken.None' explicitly is redundant — the parameter already defaults to it when omitted",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "CancellationToken.None is already what an optional CancellationToken parameter defaults to — passing it explicitly changes nothing.");

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
        var ct = context.CancellationToken;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;

        var arguments = invocation.ArgumentList.Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.NameColon is not null) continue;
            if (i >= method.Parameters.Length) continue;

            var parameter = method.Parameters[i];
            if (!parameter.IsOptional) continue;
            if (!parameter.HasExplicitDefaultValue || parameter.ExplicitDefaultValue is not null) continue;
            if (parameter.Type.ToDisplayString() != "System.Threading.CancellationToken") continue;

            if (!IsCancellationTokenNone(context.SemanticModel, argument.Expression, ct)) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, argument.GetLocation()));
        }
    }

    private static bool IsCancellationTokenNone(SemanticModel model, ExpressionSyntax expression, CancellationToken ct)
    {
        if (expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "None" }) return false;
        if (model.GetSymbolInfo(expression, ct).Symbol is not IPropertySymbol { Name: "None" } property) return false;
        return property.ContainingType?.ToDisplayString() == "System.Threading.CancellationToken";
    }
}
