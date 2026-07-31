using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `s.ToCharArray()[i]` allocates a whole new char array — copying every character in the string — just
// to read a single element back out of it. `string` already has its own indexer (`s[i]`) that reads
// the same character directly, with no allocation at all.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantToCharArrayIndexingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0103";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant ToCharArray() before indexing",
        messageFormat: "'ToCharArray()[...]' allocates a whole new char array just to read one character — index the string directly instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "ToCharArray() copies every character in the string into a new array; string's own indexer reads a character directly, with no allocation.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ElementAccessExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var elementAccess = (ElementAccessExpressionSyntax)context.Node;
        if (elementAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } toCharArrayInvocation)
            return;
        if (toCharArrayInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToCharArray" })
            return;

        if (context.SemanticModel.GetSymbolInfo(toCharArrayInvocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, elementAccess.GetLocation()));
    }
}
