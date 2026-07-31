using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят третий проход, batch) — own pick, not a Roslynator port.
// `Console.Write(x + "\n")` manually appends a newline to spell out what Console.WriteLine(x) already
// does — and does more robustly, since WriteLine uses Environment.NewLine rather than a hardcoded "\n"
// (which isn't the newline convention on every platform Console output might end up viewed on).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConsoleWriteWithNewlineAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0081";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Console.WriteLine instead of Console.Write with a manual newline",
        messageFormat: "Use 'Console.WriteLine(...)' instead of appending a newline to 'Console.Write(...)' — also uses Environment.NewLine instead of a hardcoded line ending",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Console.Write(x + \"\\n\") reimplements Console.WriteLine(x), less portably (a hardcoded \\n instead of Environment.NewLine).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Write" })
            return;
        if (invocation.ArgumentList.Arguments is not
            [
            {
                Expression: BinaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.AddExpression,
                    Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } rightLiteral,
                },
            },
            ])
        {
            return;
        }

        if (rightLiteral.Token.ValueText is not ("\n" or "\r\n")) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Console") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
