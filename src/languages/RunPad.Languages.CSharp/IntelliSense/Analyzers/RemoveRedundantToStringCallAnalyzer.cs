using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator (Apache License 2.0,
// https://github.com/dotnet/roslynator). Calling `.ToString()` on an expression whose static type is
// already `string` is a redundant no-op — always semantically identical to the expression itself.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantToStringCallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0012";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant ToString() call",
        messageFormat: "Calling ToString() on an expression that is already a string is redundant",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Calling ToString() on a value whose static type is already string does nothing.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return; // only the parameterless overload

        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" } memberAccess)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol
            { Parameters.Length: 0, Name: "ToString" })
        {
            return;
        }

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        if (receiverType is { SpecialType: SpecialType.System_String })
            context.ReportDiagnostic(Diagnostic.Create(Rule, memberAccess.Name.GetLocation()));
    }
}
