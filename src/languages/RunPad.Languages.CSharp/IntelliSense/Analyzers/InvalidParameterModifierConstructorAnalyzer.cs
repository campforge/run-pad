using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the thirtieth research subagent survey,
// re-verified independently before implementing. `System.Reflection.ParameterModifier(int
// parameterCount)` — first touch of `ParameterModifier`. Confirmed empirically: throws
// `ArgumentException` ("Must specify one or more parameters.") for `parameterCount <= 0` (both 0 and
// negative throw); `1` and higher are valid. No `[SupportedOSPlatform]` gating, no side effects — a
// trivial struct wrapping a `bool[]`. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidParameterModifierConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0503";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ParameterModifier parameterCount is not positive",
        messageFormat: "'new ParameterModifier({0})' always throws ArgumentException at runtime — parameterCount must be positive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ParameterModifier(int parameterCount) throws ArgumentException for a non-positive parameterCount.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Reflection.ParameterModifier") return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var countArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int parameterCount) return;
        if (parameterCount > 0) return;

        try
        {
            _ = new ParameterModifier(parameterCount);
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), parameterCount));
        }
    }
}
