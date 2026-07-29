using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок девятый проход, batch) — idea ported from Roslynator's
// UseMethodInsteadOfReferenceEqualsForValueTypeAnalyzer-style rules (Apache License 2.0,
// https://github.com/dotnet/roslynator). object.ReferenceEquals boxes a value-type argument before
// comparing — each boxing produces a distinct object, so this is almost always false even for two
// "equal" values, a classic gotcha. Deliberately does not flag a plain `null` literal argument (the
// legitimate, common "is this null" pattern), since a null literal has no value-type static type to
// begin with.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DoNotUseReferenceEqualsWithValueTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0032";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Do not use ReferenceEquals with a value type",
        messageFormat: "'ReferenceEquals' boxes value-type arguments before comparing — each boxing creates a distinct object, so this is almost always false",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "object.ReferenceEquals boxes a value-type argument before comparing, so two equal values almost never compare equal.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 2) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                IsStatic: true,
                Name: "ReferenceEquals",
                ContainingType.SpecialType: SpecialType.System_Object,
            })
        {
            return;
        }

        var ct = context.CancellationToken;
        var arg0Type = context.SemanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression, ct).Type;
        var arg1Type = context.SemanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[1].Expression, ct).Type;

        if (IsValueType(arg0Type) || IsValueType(arg1Type))
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsValueType(ITypeSymbol? type) => type is { IsValueType: true };
}
