using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 150, batch) — a classic, very well-known C# gotcha (not a
// Roslynator port — same "real bug, near-zero false-positive risk" bar as SelfReferencingPropertyAnalyzer):
// `catch (Exception ex) { throw ex; }` resets the exception's stack trace to this throw site, hiding
// where it originally came from. `throw;` (no expression) rethrows the SAME exception object without
// touching its stack trace at all — the fix is always just deleting three characters.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RethrowToPreserveStackTraceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0035";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Rethrowing loses the original stack trace",
        messageFormat: "'throw {0};' resets the stack trace to this line — use a bare 'throw;' to preserve where the exception actually came from",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "'throw ex;' resets the exception's stack trace to the rethrow site; a bare 'throw;' rethrows the same exception unchanged.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ThrowStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var throwStatement = (ThrowStatementSyntax)context.Node;
        if (throwStatement.Expression is not IdentifierNameSyntax identifier) return;

        CatchClauseSyntax? catchClause = null;
        for (var node = throwStatement.Parent; node is not null; node = node.Parent)
        {
            if (node is CatchClauseSyntax cc) { catchClause = cc; break; }
            if (node is MethodDeclarationSyntax or LocalFunctionStatementSyntax
                or LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                return;
            }
        }
        if (catchClause?.Declaration is not { } declaration) return;
        if (declaration.Identifier.Text != identifier.Identifier.Text) return;

        var ct = context.CancellationToken;
        var throwSymbol = context.SemanticModel.GetSymbolInfo(identifier, ct).Symbol;
        var catchSymbol = context.SemanticModel.GetDeclaredSymbol(declaration, ct);
        if (throwSymbol is null || !SymbolEqualityComparer.Default.Equals(throwSymbol, catchSymbol)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, throwStatement.GetLocation(), identifier.Identifier.Text));
    }
}
