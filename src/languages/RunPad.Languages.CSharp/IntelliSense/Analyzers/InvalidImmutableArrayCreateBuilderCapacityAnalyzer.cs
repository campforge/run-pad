using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #4 from the eighteenth research subagent survey,
// re-verified independently before implementing. `System.Collections.Immutable.ImmutableArray.
// CreateBuilder<T>(int initialCapacity)` — a static factory method, confirmed empirically to throw
// `ArgumentOutOfRangeException` for a negative `initialCapacity`; `0` and positive values are valid. A
// genuinely fresh gap: `NegativeCapacityArgumentAnalyzer` (RPAD0231) only registers on
// `ObjectCreationExpression`/`ImplicitObjectCreationExpression` syntax kinds (constructors) — this is a
// static-method `InvocationExpression`, syntactically outside that analyzer's scope. Confirmed via
// reflection that none of the sibling immutable-collection types (`ImmutableList`/`ImmutableHashSet`/
// `ImmutableSortedSet`/`ImmutableDictionary`/`ImmutableSortedDictionary`) have a capacity-taking
// `CreateBuilder` overload — only `ImmutableArray` has this shape, so no batch opportunity across
// siblings here. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects — a pure managed
// allocation, no filesystem/network/OS interaction.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidImmutableArrayCreateBuilderCapacityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0464";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ImmutableArray.CreateBuilder capacity argument always throws",
        messageFormat: "'ImmutableArray.CreateBuilder<...>({0})' always throws ArgumentOutOfRangeException at runtime — initialCapacity cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ImmutableArray.CreateBuilder<T>(int initialCapacity) throws ArgumentOutOfRangeException for a negative capacity.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "CreateBuilder" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var capacityArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(capacityArg, ct).Value is not int capacity) return;
        if (capacity >= 0) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "CreateBuilder",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Collections.Immutable.ImmutableArray") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), capacity));
    }
}
