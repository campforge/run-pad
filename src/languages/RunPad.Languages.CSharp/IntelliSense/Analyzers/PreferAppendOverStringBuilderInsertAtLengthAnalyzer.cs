using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести пятьдесят девятый проход, batch) — own pick, not a Roslynator port. Same
// "inserting at the position right after the end is just appending" theme as
// PreferListAddOverInsertAtCountAnalyzer (RPAD0164), for StringBuilder instead of List<T>:
// `sb.Insert(sb.Length, value)` inserts at the position immediately past the current content — exactly
// what `sb.Append(value)` already does, more directly and without the general insert-at-arbitrary-
// position machinery. Matched structurally (the receiver of Insert and the receiver of Length must be
// the literal same expression — SyntaxNode.IsEquivalentTo).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferAppendOverStringBuilderInsertAtLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0252";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Append over StringBuilder.Insert at Length",
        messageFormat: "'sb.Insert(sb.Length, value)' can be replaced with 'sb.Append(value)' — inserting at the position right after the end is the same as appending",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "StringBuilder.Insert(sb.Length, value) inserts immediately past the current content, which is exactly what Append(value) already does.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Insert", Expression: var insertReceiver })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Length", Expression: var lengthReceiver } },
                _,
            ])
        {
            return;
        }
        if (!insertReceiver.IsEquivalentTo(lengthReceiver)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Insert" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
