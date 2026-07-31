using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят третий проход, batch) — same "recognize the redundant format string"
// theme as RedundantStringFormatCallAnalyzer (RPAD0073, проход 162), for the single-placeholder case
// instead of the no-placeholder one: `string.Format("{0}", x)` calls into the whole format-parsing
// machinery just to substitute x in as the entire result — `$"{x}"` (string interpolation) does the
// exact same null-to-empty-string conversion string.Format's composite formatting does, more directly.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantSingleArgStringFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0127";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant single-placeholder string.Format call",
        messageFormat: "Use string interpolation instead of 'string.Format' with a format string that only substitutes its single argument",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "string.Format with a format string that has exactly one placeholder and nothing else formats a single value for no reason — string interpolation does the same substitution directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Format" }) return;
        if (invocation.ArgumentList.Arguments is not
            [{ Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } formatLiteral }, _])
        {
            return;
        }
        if (formatLiteral.Token.ValueText != "{0}") return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String || method.Name != "Format") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
