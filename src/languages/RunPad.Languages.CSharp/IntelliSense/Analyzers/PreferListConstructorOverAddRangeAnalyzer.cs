using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят девятый проход, batch) — own pick, not a Roslynator port.
// `var list = new List<T>(); list.AddRange(source);` builds an empty list and then immediately grows
// it to hold `source` — the `List<T>(IEnumerable<T>)` constructor does the same thing in one step,
// without the empty intermediate state. Scoped to the narrowest safe shape: a local declared with the
// parameterless List<T> constructor, whose VERY NEXT sibling statement is a single AddRange call on
// that exact local and nothing else — anything in between (even an unrelated statement) could depend
// on the list being empty first, so this deliberately doesn't look past the immediately next statement.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferListConstructorOverAddRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0152";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the List<T> collection constructor over AddRange right after construction",
        messageFormat: "'new List<T>()' immediately followed by 'AddRange(...)' can be combined into 'new List<T>(...)'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "new List<T>() followed immediately by AddRange(source) builds an empty list only to grow it right away — the List<T>(IEnumerable<T>) constructor does both in one step.");

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
        if (constructor.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
            return;

        if (declaration.Parent is not BlockSyntax { Statements: var statements }) return;
        var index = statements.IndexOf(declaration);
        if (index < 0 || index + 1 >= statements.Count) return;

        if (statements[index + 1] is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "AddRange", Expression: IdentifierNameSyntax receiverId },
                    ArgumentList.Arguments.Count: 1,
                } addRangeInvocation,
            })
        {
            return;
        }
        if (receiverId.Identifier.Text != variable.Identifier.Text) return;

        if (context.SemanticModel.GetSymbolInfo(addRangeInvocation, ct).Symbol is not IMethodSymbol { Name: "AddRange" } addRangeMethod)
            return;
        if (addRangeMethod.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.List<T>")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, declaration.GetLocation()));
    }
}
