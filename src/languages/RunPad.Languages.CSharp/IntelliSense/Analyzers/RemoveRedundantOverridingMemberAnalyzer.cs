using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок восьмой проход, batch) — idea ported from Roslynator's
// RemoveRedundantOverridingMemberAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator).
// An override whose ENTIRE body is a single call to base.Method(...), passing every parameter through
// unchanged and in order, behaves exactly like not overriding at all — pure clutter, most often left
// behind by a code-gen template or an IDE's "override members" action that was never filled in.
// Deliberately narrow: bails out entirely if any parameter has a ref/out/in modifier (matching those
// up correctly adds real complexity for a case rare enough not to be worth the added risk here).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantOverridingMemberAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0024";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant overriding member",
        messageFormat: "This override only calls the base member with the same arguments — it behaves identically to not overriding at all",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An override whose entire body forwards to base with the same arguments adds nothing over inheriting the base implementation.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (!method.Modifiers.Any(SyntaxKind.OverrideKeyword)) return;

        var parameters = method.ParameterList.Parameters;
        if (parameters.Any(p => p.Modifiers.Count > 0)) return; // ref/out/in — deliberately not handled

        var invocation = ExtractSoleBaseCall(method);
        if (invocation is null) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Expression: BaseExpressionSyntax,
                Name.Identifier.Text: var calledName,
            } || calledName != method.Identifier.Text)
        {
            return;
        }

        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count != parameters.Count) return;

        for (var i = 0; i < parameters.Count; i++)
        {
            if (arguments[i].NameColon is not null) return;
            if (arguments[i].Expression is not IdentifierNameSyntax id) return;
            if (id.Identifier.Text != parameters[i].Identifier.Text) return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation()));
    }

    private static InvocationExpressionSyntax? ExtractSoleBaseCall(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is { Expression: InvocationExpressionSyntax exprBodyInvocation })
            return exprBodyInvocation;

        if (method.Body is { Statements: [var single] })
        {
            return single switch
            {
                ExpressionStatementSyntax { Expression: InvocationExpressionSyntax voidInvocation } => voidInvocation,
                ReturnStatementSyntax { Expression: InvocationExpressionSyntax returnInvocation } => returnInvocation,
                _ => null,
            };
        }

        return null;
    }
}
