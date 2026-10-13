using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `var sw = new Stopwatch(); sw.Start();` constructs a stopped Stopwatch and then immediately starts
// it — `Stopwatch.StartNew()` does both in one call, and is the API the type was designed to offer for
// exactly this. Scoped to the narrowest safe shape (matching PreferListConstructorOverAddRangeAnalyzer): a
// parameterless `new Stopwatch()` local declaration whose VERY NEXT sibling statement is
// a parameterless `.Start()` call on that exact local, and nothing else in between.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferStopwatchStartNewOverConstructorThenStartAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0207";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Stopwatch.StartNew() over the constructor followed by Start()",
        messageFormat: "'new Stopwatch()' immediately followed by 'Start()' can be combined into 'Stopwatch.StartNew()'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "new Stopwatch() followed immediately by Start() constructs a stopped Stopwatch only to start it right away — Stopwatch.StartNew() does both in one call.");

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
        if (constructor.ContainingType?.ToDisplayString() != "System.Diagnostics.Stopwatch")
            return;

        if (declaration.Parent is not BlockSyntax { Statements: var statements }) return;
        var index = statements.IndexOf(declaration);
        if (index < 0 || index + 1 >= statements.Count) return;

        if (statements[index + 1] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Start", Expression: IdentifierNameSyntax receiverId },
                    ArgumentList.Arguments.Count: 0,
                } startInvocation,
            })
        {
            return;
        }
        if (receiverId.Identifier.Text != variable.Identifier.Text) return;

        if (context.SemanticModel.GetSymbolInfo(startInvocation, ct).Symbol is not IMethodSymbol { Name: "Start" } startMethod)
            return;
        if (startMethod.ContainingType?.ToDisplayString() != "System.Diagnostics.Stopwatch")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.GetLocation()));
    }
}
