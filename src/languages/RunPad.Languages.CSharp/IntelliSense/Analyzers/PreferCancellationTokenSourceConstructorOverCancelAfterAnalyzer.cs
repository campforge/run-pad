using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `var cts = new CancellationTokenSource(); cts.CancelAfter(delay);` constructs a CancellationTokenSource
// that never times out and then immediately arms a timeout on it — `new CancellationTokenSource(delay)`
// does both in one call, which is exactly what that constructor overload exists for. Scoped to the
// narrowest safe shape (matching PreferStopwatchStartNewOverConstructorThenStartAnalyzer):
// a parameterless constructor local declaration whose VERY NEXT sibling statement is a single-argument
// `.CancelAfter(...)` call on that exact local, and nothing else in between.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferCancellationTokenSourceConstructorOverCancelAfterAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0210";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the CancellationTokenSource(delay) constructor over CancelAfter right after construction",
        messageFormat: "'new CancellationTokenSource()' immediately followed by 'CancelAfter(delay)' can be combined into 'new CancellationTokenSource(delay)'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "new CancellationTokenSource() followed immediately by CancelAfter(delay) arms a timeout right after constructing an untimed source — new CancellationTokenSource(delay) does both in one call.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LocalDeclarationStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;
        if (declaration.Declaration.Variables is not [{ Initializer.Value: ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 0 } objectCreation } variable])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(objectCreation, ct).Symbol is not IMethodSymbol
            { MethodKind: MethodKind.Constructor } constructor)
        {
            return;
        }
        if (constructor.ContainingType?.ToDisplayString() != "System.Threading.CancellationTokenSource")
            return;

        if (declaration.Parent is not BlockSyntax { Statements: var statements }) return;
        var index = statements.IndexOf(declaration);
        if (index < 0 || index + 1 >= statements.Count) return;

        if (statements[index + 1] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "CancelAfter", Expression: IdentifierNameSyntax receiverId },
                    ArgumentList.Arguments.Count: 1,
                } cancelAfterInvocation,
            })
        {
            return;
        }
        if (receiverId.Identifier.Text != variable.Identifier.Text) return;

        if (context.SemanticModel.GetSymbolInfo(cancelAfterInvocation, ct).Symbol is not IMethodSymbol { Name: "CancelAfter" } cancelAfterMethod)
            return;
        if (cancelAfterMethod.ContainingType?.ToDisplayString() != "System.Threading.CancellationTokenSource")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.GetLocation()));
    }
}
