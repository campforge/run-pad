using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 148, batch) — idea ported from Roslynator's
// AvoidThrowingExceptionFromFinallyBlockAnalyzer (Apache License 2.0,
// https://github.com/dotnet/roslynator). A `throw` directly inside a `finally` block silently
// replaces (masks) whatever exception was already propagating through the try/catch it belongs to —
// a genuinely surprising, well-known .NET pitfall, not a style nitpick.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DoNotThrowFromFinallyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0028";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Do not throw from a finally block",
        messageFormat: "Throwing from a finally block replaces (masks) any exception already propagating through the try/catch it belongs to",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An exception thrown directly in a finally block silently replaces whatever exception was already in flight.");

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

        for (var node = throwStatement.Parent; node is not null; node = node.Parent)
        {
            // A nested try (its own try/catch/finally) inside the outer finally may handle this
            // itself — not this analyzer's concern once we've descended into one.
            if (node is TryStatementSyntax) return;

            if (node is FinallyClauseSyntax)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, throwStatement.GetLocation()));
                return;
            }

            // A lambda/local function/method boundary means we've left the finally block's own
            // control flow — it runs on its own stack, "inside finally" no longer applies.
            if (node is MethodDeclarationSyntax or LocalFunctionStatementSyntax
                or LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                return;
            }
        }
    }
}
