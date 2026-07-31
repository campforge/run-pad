using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 160, batch) — own pick, not a Roslynator port.
// `Enum.GetValues(typeof(T)).Cast<T>()` is the pre-.NET-5 idiom for getting a strongly-typed sequence
// of an enum's values — the non-generic Enum.GetValues(Type) overload returns a boxed Array, so Cast<T>
// unboxes every element afterward. `Enum.GetValues<T>()` (generic, since .NET 5 — always available
// given RunPad's .NET 6+ SDK floor, DotnetSdkProvider.MinSupportedSdkMajorVersion) returns
// a strongly-typed T[] directly, no typeof/boxing/Cast round-trip needed.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EnumGetValuesCastAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0067";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use the generic Enum.GetValues<T>() overload",
        messageFormat: "Use 'Enum.GetValues<T>()' instead of 'Enum.GetValues(typeof(T)).Cast<T>()' — avoids the boxing/unboxing round-trip",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Enum.GetValues(Type) returns a boxed Array; the generic Enum.GetValues<T>() overload returns a strongly-typed T[] directly.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.Text: "Cast", TypeArgumentList.Arguments: [var castTypeArg] },
                Expression: InvocationExpressionSyntax innerInvocation,
            })
        {
            return;
        }

        if (innerInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetValues" })
            return;
        if (innerInvocation.ArgumentList.Arguments is not [{ Expression: TypeOfExpressionSyntax typeOfExpression }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(innerInvocation, ct).Symbol is not IMethodSymbol getValuesMethod)
            return;
        if (getValuesMethod.IsGenericMethod || getValuesMethod.ContainingType?.ToDisplayString() != "System.Enum")
            return;

        var typeOfType = context.SemanticModel.GetTypeInfo(typeOfExpression.Type, ct).Type;
        var castType = context.SemanticModel.GetTypeInfo(castTypeArg, ct).Type;
        if (typeOfType is not { TypeKind: TypeKind.Enum }) return;
        if (!SymbolEqualityComparer.Default.Equals(typeOfType, castType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
