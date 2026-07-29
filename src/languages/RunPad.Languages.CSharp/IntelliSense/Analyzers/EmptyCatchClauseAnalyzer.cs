using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок второй проход) — idea ported (not copied — own implementation against
// RunPad's own analyzer pipeline, see RoslynAnalyzerEngine) from Roslynator's
// AvoidEmptyCatchClauseThatCatchesSystemExceptionAnalyzer (RCS1075, Apache License 2.0,
// https://github.com/dotnet/roslynator). A catch clause with an empty body that catches everything —
// either a bare `catch { }` or `catch (Exception) { }`/`catch (System.Exception ex) { }` — silently
// swallows errors the author almost certainly didn't mean to hide, including ones they never
// anticipated. Notebook-scenario-relevant: pasted/generated code with a leftover empty catch is a
// common source of "why did nothing happen" confusion when a script's output just goes quiet.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyCatchClauseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Empty catch clause that catches System.Exception",
        messageFormat: "Empty catch clause that catches System.Exception should be avoided",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Catching System.Exception (or catching without specifying a type) and doing " +
            "nothing with it silently swallows every error, including ones you didn't anticipate.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeCatchClause, SyntaxKind.CatchClause);
    }

    private static void AnalyzeCatchClause(SyntaxNodeAnalysisContext context)
    {
        var catchClause = (CatchClauseSyntax)context.Node;

        // A `when (...)` filter makes this a deliberately conditional catch, not a blanket swallow —
        // even with an empty body, the filter itself is doing something.
        if (catchClause.Filter is not null) return;
        if (catchClause.Block.Statements.Count > 0) return;

        var declaration = catchClause.Declaration;
        if (declaration is null)
        {
            // Bare `catch { }` catches literally everything, including exceptions that aren't even
            // System.Exception-derived (e.g. a corrupted-state exception on older runtimes).
            context.ReportDiagnostic(Diagnostic.Create(Rule, catchClause.CatchKeyword.GetLocation()));
            return;
        }

        var type = context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type;
        if (type is not null && type.ToDisplayString() == "System.Exception")
            context.ReportDiagnostic(Diagnostic.Create(Rule, catchClause.CatchKeyword.GetLocation()));
    }
}
