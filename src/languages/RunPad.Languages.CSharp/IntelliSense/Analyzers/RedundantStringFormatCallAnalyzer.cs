using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `string.Format("no placeholders here")` calls into the whole format-parsing machinery just to
// return the literal unchanged — there's nothing to substitute. Deliberately narrow: only the
// single-argument overload with a literal string containing no '{' at all (so no risk of
// misjudging an escaped `{{` or a genuine placeholder).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantStringFormatCallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0073";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant string.Format call",
        messageFormat: "'string.Format' with no placeholders is redundant — use the string literal directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "string.Format(literal) with a format string that contains no placeholders just returns the literal unchanged.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Format" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal }])
            return;
        if (literal.Token.ValueText.Contains('{')) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String || method.Name != "Format") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
