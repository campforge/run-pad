using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `new Regex(pattern, RegexOptions.None)`/`Regex.IsMatch(input, pattern, RegexOptions.None)` (and the
// other static Regex helpers) spell out the exact options value already used when no options are
// given — genuinely redundant. Covers both the constructor and the static helper methods, since both
// have an options-less overload the explicit `.None` argument could simply be dropped in favor of.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantRegexOptionsNoneAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0125";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant RegexOptions.None argument",
        messageFormat: "Passing 'RegexOptions.None' explicitly is redundant — it's already the default when no options are given",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "RegexOptions.None is what Regex's constructor and static helpers already use when no options argument is supplied.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList?.Arguments is not [_, { Expression: var lastArgument }]) return;
        if (!IsRegexOptionsNone(context, lastArgument)) return;

        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Text.RegularExpressions.Regex") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax) return;
        if (invocation.ArgumentList.Arguments.Count < 2) return;

        var lastArgument = invocation.ArgumentList.Arguments[^1].Expression;
        if (!IsRegexOptionsNone(context, lastArgument)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        if (method.ContainingType?.ToDisplayString() != "System.Text.RegularExpressions.Regex") return;
        if (method.Parameters is not [.., { Type: { } lastParamType }]
            || lastParamType.ToDisplayString() != "System.Text.RegularExpressions.RegexOptions")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsRegexOptionsNone(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "None" } access) return false;
        return context.SemanticModel.GetSymbolInfo(access, context.CancellationToken).Symbol is IFieldSymbol { ContainingType: { } type }
            && type.ToDisplayString() == "System.Text.RegularExpressions.RegexOptions";
    }
}
