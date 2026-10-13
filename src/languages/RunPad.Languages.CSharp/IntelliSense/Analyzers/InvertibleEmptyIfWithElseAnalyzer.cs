using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `if (cond) { } else { doStuff(); }` — an empty if-branch next to a real else-branch — says the same
// thing as `if (!cond) { doStuff(); }`, just with an extra empty block and an inverted-feeling
// condition to read past. Scoped to the case where the else branch is NOT itself empty (an empty
// if with an empty else is RemoveEmptyElseClauseAnalyzer's (RPAD0009) territory, not this one's).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvertibleEmptyIfWithElseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0116";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Empty if branch next to a non-empty else",
        messageFormat: "The 'if' branch is empty — invert the condition and use the 'else' branch's body directly, removing the empty branch",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An empty if-branch next to a real else-branch says the same thing as inverting the condition and dropping the empty branch, more directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.IfStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;
        if (ifStatement.Else is not { } elseClause) return;
        if (elseClause.Statement is IfStatementSyntax) return; // `else if` — not this analyzer's business
        if (!IsEmptyStatement(ifStatement.Statement)) return;
        if (IsEmptyStatement(elseClause.Statement)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, ifStatement.IfKeyword.GetLocation()));
    }

    private static bool IsEmptyStatement(StatementSyntax statement) => statement switch
    {
        BlockSyntax { Statements.Count: 0 } => true,
        EmptyStatementSyntax => true,
        _ => false,
    };
}
