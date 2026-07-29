using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок седьмой проход, batch) — idea ported from Roslynator (Apache License 2.0,
// https://github.com/dotnet/roslynator). A lone `;` as a full statement (not the empty body of a
// `for`/`while`, which uses the same syntax node but is a deliberate idiom — excluded below) is almost
// always an accidental leftover semicolon from editing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveEmptyStatementAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0011";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Unnecessary empty statement",
        messageFormat: "Unnecessary empty statement ';' can be removed",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A lone semicolon statement is almost always an accidental leftover from editing.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EmptyStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        // `for (...) ;` / `while (...) ;` — an EmptyStatement used deliberately as a loop's whole
        // body (the loop does all its work in the condition/increment clauses) — a real, if unusual,
        // idiom, not an accident. Only the parent shapes matter here, not what's further up.
        if (context.Node.Parent is ForStatementSyntax or WhileStatementSyntax or ForEachStatementSyntax)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation()));
    }
}
