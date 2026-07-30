using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят восьмой проход, batch) — own pick, not a Roslynator port. Same
// "preserve exception diagnostic information" theme as RethrowToPreserveStackTraceAnalyzer (RPAD0035,
// проход 150), for a different mistake: `throw new Exception(ex.Message)` inside a catch block copies
// only the MESSAGE TEXT of the original exception into a brand-new one — the original's type, stack
// trace, and any custom data are all gone. `throw;` (a bare rethrow) preserves everything; `throw new
// Exception(ex.Message, ex)` (passing ex as the innerException too) preserves the original as a
// traceable inner exception. Deliberately only flags when `ex` itself is NOT passed anywhere in the
// argument list — if it is (even just as the innerException), the original is already preserved and
// this isn't the mistake being caught here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LossyExceptionWrapAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0106";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Exception wrapped using only its Message loses diagnostic information",
        messageFormat: "Wrapping in a new exception using only '{0}.Message' discards the original exception's type and stack trace — use 'throw;' to rethrow, or pass '{0}' as the inner exception",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "throw new Exception(ex.Message) keeps only the message text — the original exception's type, stack trace, and any custom data are lost.");

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
        if (throwStatement.Expression is not ObjectCreationExpressionSyntax { ArgumentList.Arguments: { } arguments } creation)
            return;

        var catchClause = throwStatement.FirstAncestorOrSelf<CatchClauseSyntax>();
        if (catchClause?.Declaration is not { } declaration || declaration.Identifier.IsMissing) return;
        var caughtName = declaration.Identifier.Text;

        var referencesMessage = arguments.Any(a =>
            a.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Message", Expression: IdentifierNameSyntax id }
            && id.Identifier.Text == caughtName);
        if (!referencesMessage) return;

        var referencesExceptionItself = arguments.Any(a =>
            a.Expression is IdentifierNameSyntax id && id.Identifier.Text == caughtName);
        if (referencesExceptionItself) return;

        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol ctor)
            return;
        if (!IsExceptionType(ctor.ContainingType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, throwStatement.GetLocation(), caughtName));
    }

    private static bool IsExceptionType(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Exception") return true;
        }
        return false;
    }
}
