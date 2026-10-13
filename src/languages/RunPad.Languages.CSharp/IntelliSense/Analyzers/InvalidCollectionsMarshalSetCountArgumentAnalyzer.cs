using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Runtime.InteropServices.CollectionsMarshal.
// SetCount<T>(List<T> list, int count)` — first touch of `CollectionsMarshal`. Confirmed empirically:
// negative `count` always throws `ArgumentOutOfRangeException` (param `count`, populated); `0` and
// positive values are valid. Confirmed NOT covered by `NegativeCapacityArgumentAnalyzer` (RPAD0231,
// scoped to `ObjectCreationExpression` constructors only — this is a static method call, syntactically
// outside that analyzer's scope, same "outside scope" reasoning already used for
// `InvalidImmutableArrayCreateBuilderCapacityAnalyzer`/RPAD0464). `int` parameter — const-capable. No
// `[SupportedOSPlatform]` gating, no side effects — pure in-memory `List<T>` bookkeeping.
// Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCollectionsMarshalSetCountArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0511";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CollectionsMarshal.SetCount count is negative",
        messageFormat: "'CollectionsMarshal.SetCount(..., {0})' always throws ArgumentOutOfRangeException at runtime — count must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CollectionsMarshal.SetCount<T>(List<T>, int count) throws ArgumentOutOfRangeException for a negative count.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SetCount" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "SetCount",
                IsStatic: true,
                ContainingType: { } containingType,
                Parameters: [_, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Runtime.InteropServices.CollectionsMarshal") return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var countArg }]) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;
        if (count >= 0) return;

        var list = new List<int>();
        try
        {
            CollectionsMarshal.SetCount(list, count);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), count));
        }
    }
}
