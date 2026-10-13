using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #4 from the twenty-sixth research subagent survey,
// re-verified independently before implementing. `System.Runtime.CompilerServices.RuntimeHelpers.
// AllocateTypeAssociatedMemory(Type type, int size)` — first analyzer in the catalog to touch
// `RuntimeHelpers`. Confirmed empirically: negative `size` always throws
// `ArgumentOutOfRangeException` (param `size`); `0` is valid and performs a real (harmless) allocation.
// Confirmed the negative-size path throws BEFORE any allocation occurs (verified by contrast against
// the `size=0` case, which does allocate) — side-effect-safe to probe. No `[SupportedOSPlatform]`
// gating (reflection: only nullable-context/extension attributes). Confirmed invariant across
// en-US/de-DE/ar-SA/ja-JP. The `type` argument isn't literal-driven and doesn't need to be — any valid
// `typeof(...)` expression is used as the non-interesting probe argument.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRuntimeHelpersAllocateTypeAssociatedMemoryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0491";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "RuntimeHelpers.AllocateTypeAssociatedMemory size is negative",
        messageFormat: "'RuntimeHelpers.AllocateTypeAssociatedMemory(...)' always throws ArgumentOutOfRangeException at runtime — size must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RuntimeHelpers.AllocateTypeAssociatedMemory(Type, int size) throws ArgumentOutOfRangeException for a negative size.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "AllocateTypeAssociatedMemory" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "AllocateTypeAssociatedMemory",
                IsStatic: true,
                ContainingType: { } containingType,
                Parameters: [_, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Runtime.CompilerServices.RuntimeHelpers") return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var sizeArg }]) return;
        if (context.SemanticModel.GetConstantValue(sizeArg, ct).Value is not int size) return;
        if (size >= 0) return;

        try
        {
            _ = RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(object), size);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
    }
}
