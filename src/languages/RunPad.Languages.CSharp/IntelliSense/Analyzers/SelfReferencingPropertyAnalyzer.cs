using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// A common, real copy-paste bug (not a Roslynator
// port — this specific shape isn't in its catalog, but fits the same "looks like a real bug, near-zero
// false-positive risk" bar every analyzer in this series is held to): a property whose getter returns
// ITSELF (`get { return Foo; }` / `=> Foo;`, not a backing field) — infinite recursion, guaranteed
// StackOverflowException the moment it's read. Almost always a copy-paste of a backing-field pattern
// where the field reference was never actually typed in (`_foo` → `Foo`).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SelfReferencingPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0025";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Property getter returns itself",
        messageFormat: "This getter returns the property itself — reading it will recurse forever and crash with a StackOverflowException",
        category: "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A property getter whose body is just the property's own name recurses infinitely the moment it's read.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.PropertyDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var property = (PropertyDeclarationSyntax)context.Node;

        var getterExpression = ExtractGetterExpression(property);
        if (getterExpression is not IdentifierNameSyntax identifier) return;
        if (identifier.Identifier.Text != property.Identifier.Text) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, identifier.GetLocation()));
    }

    private static ExpressionSyntax? ExtractGetterExpression(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody is { Expression: { } exprBody }) return exprBody;

        var getter = property.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        return getter switch
        {
            { ExpressionBody.Expression: { } accessorExprBody } => accessorExprBody,
            { Body.Statements: [ReturnStatementSyntax { Expression: { } returned }] } => returned,
            _ => null,
        };
    }
}
