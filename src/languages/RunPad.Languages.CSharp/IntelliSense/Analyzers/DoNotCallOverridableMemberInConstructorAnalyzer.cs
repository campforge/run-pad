using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 147, batch) — idea ported from Roslynator's
// DoNotCallOverridableMemberInConstructorAnalyzer (Apache License 2.0,
// https://github.com/dotnet/roslynator). A virtual/abstract/overridable call from a constructor can
// dispatch to a DERIVED class's override before that derived class's own field initializers have run —
// a well-known, genuinely surprising C# pitfall, not a style nitpick. Skipped entirely for sealed
// types (nothing can override anything there, so the call is safe) — the single biggest source of
// false positives this check could otherwise have.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DoNotCallOverridableMemberInConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0018";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Do not call an overridable member from a constructor",
        messageFormat: "'{0}' can be overridden — calling it from a constructor may run a derived class's " +
            "override before that derived class has finished initializing its own fields",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A virtual/abstract call from a constructor can dispatch to a derived class's override before that class's own field initializers have run.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ConstructorDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ctor = (ConstructorDeclarationSyntax)context.Node;
        SyntaxNode? body = ctor.Body ?? (SyntaxNode?)ctor.ExpressionBody;
        if (body is null) return;

        var semanticModel = context.SemanticModel;
        var ct = context.CancellationToken;
        if (semanticModel.GetDeclaredSymbol(ctor, ct)?.ContainingType is not { IsSealed: false } containingType)
            return;

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var targetName = invocation.Expression switch
            {
                IdentifierNameSyntax => invocation.Expression,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name,
                _ => null,
            };
            if (targetName is null) continue;

            if (semanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary,
                } method)
            {
                continue;
            }

            var isOverridable = method.IsVirtual || method.IsAbstract || (method.IsOverride && !method.IsSealed);
            if (!isOverridable) continue;
            if (!IsSameOrBaseType(containingType, method.ContainingType)) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), method.Name));
        }
    }

    private static bool IsSameOrBaseType(INamedTypeSymbol type, INamedTypeSymbol? candidate)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, candidate)) return true;
        }

        return false;
    }
}
