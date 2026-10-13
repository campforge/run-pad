using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #5 from the nineteenth research subagent survey,
// re-verified independently before implementing. `ImmutableArray<T>.Builder.RemoveRange(int index, int
// length)`/`.Insert(int index, T item)`/`.CopyTo(T[] array, int index)`/`.CopyTo(int sourceIndex, T[]
// array, int destinationIndex, int length)` — confirmed empirically all throw
// `ArgumentOutOfRangeException` for a negative `index`/`length`/`sourceIndex`/`destinationIndex`.
// Deliberately scoped ONLY to the negative-literal case — NEVER the "index/length exceeds the builder's
// current Count" case, which depends on the builder's actual runtime element count that this analyzer
// cannot know statically, same schema/state-dependence exclusion already established for
// `ImmutableArray.Create(items, start, length)` (RPAD0465). Confirmed no side effects beyond the
// builder's own backing array (a pure managed mutation). Confirmed invariant across en-US/de-DE/ar-SA/
// ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidImmutableArrayBuilderRangeArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0470";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ImmutableArray.Builder method argument is negative",
        messageFormat: "'{0}.{1}(...)' always throws ArgumentOutOfRangeException at runtime — {2} ({3}) cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ImmutableArray<T>.Builder.RemoveRange/Insert/CopyTo throw ArgumentOutOfRangeException for a negative index/length/sourceIndex/destinationIndex argument.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> MethodNames = ImmutableHashSet.Create("RemoveRange", "Insert", "CopyTo");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        if (!MethodNames.Contains(methodName)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType, Parameters: { } parameters } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Collections.Immutable.ImmutableArray<T>.Builder") return;

        var args = invocation.ArgumentList.Arguments;
        for (var i = 0; i < parameters.Length && i < args.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.Type.SpecialType != SpecialType.System_Int32) continue;
            if (parameter.Name is not ("index" or "length" or "sourceIndex" or "destinationIndex")) continue;

            if (context.SemanticModel.GetConstantValue(args[i].Expression, ct).Value is int value && value < 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "ImmutableArray<T>.Builder", methodName, parameter.Name, value));
                return;
            }
        }
    }
}
