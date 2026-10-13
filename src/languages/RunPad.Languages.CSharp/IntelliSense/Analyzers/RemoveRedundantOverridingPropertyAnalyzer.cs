using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Same idea as RemoveRedundantOverridingMemberAnalyzer
// (RPAD0024, pass 148), extended to properties: an override whose getter/setter each do nothing but
// forward to base.Property/base.Property = value behaves exactly like not overriding at all.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantOverridingPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0049";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant overriding property",
        messageFormat: "This property override only forwards to the base property — it behaves identically to not overriding at all",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A property override whose every accessor does nothing but forward to the base property adds nothing over inheriting the base implementation.");

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
        if (!property.Modifiers.Any(SyntaxKind.OverrideKeyword)) return;

        if (property.ExpressionBody is { Expression: { } exprBodyExpr })
        {
            if (IsBasePropertyAccess(exprBodyExpr, property.Identifier.Text))
                context.ReportDiagnostic(Diagnostic.Create(Rule, property.Identifier.GetLocation()));
            return;
        }

        if (property.AccessorList is not { Accessors: { Count: > 0 } accessors }) return;

        foreach (var accessor in accessors)
        {
            var isRedundant = accessor.Kind() switch
            {
                SyntaxKind.GetAccessorDeclaration => ExtractSoleReturnOrExpressionBody(accessor) is { } getterExpr
                    && IsBasePropertyAccess(getterExpr, property.Identifier.Text),
                SyntaxKind.SetAccessorDeclaration => IsRedundantSetter(accessor, property.Identifier.Text),
                _ => false,
            };
            if (!isRedundant) return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, property.Identifier.GetLocation()));
    }

    private static ExpressionSyntax? ExtractSoleReturnOrExpressionBody(AccessorDeclarationSyntax accessor)
    {
        if (accessor.ExpressionBody is { Expression: { } expr }) return expr;
        if (accessor.Body is { Statements: [ReturnStatementSyntax { Expression: { } returned }] }) return returned;
        return null;
    }

    private static bool IsRedundantSetter(AccessorDeclarationSyntax accessor, string propertyName)
    {
        var assignmentExpr = accessor.ExpressionBody?.Expression;
        if (assignmentExpr is null && accessor.Body is { Statements: [ExpressionStatementSyntax { Expression: { } stmtExpr }] })
            assignmentExpr = stmtExpr;

        return assignmentExpr is AssignmentExpressionSyntax
        {
            Right: IdentifierNameSyntax { Identifier.Text: "value" },
        } assignment && IsBasePropertyAccess(assignment.Left, propertyName);
    }

    private static bool IsBasePropertyAccess(ExpressionSyntax expression, string propertyName) =>
        expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax, Name.Identifier.Text: var name }
        && name == propertyName;
}
