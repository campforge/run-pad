using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the thirty-fifth research subagent survey,
// re-verified independently before implementing. `System.Collections.Concurrent.Partitioner.Create`
// range-based overloads (`int`/`long`, 2-arg and 3-arg each), bundled under one diagnostic (precedent
// RPAD0426). Confirmed empirically, all four overloads individually (not extrapolated): throws
// `ArgumentOutOfRangeException` (param `toExclusive`) whenever `toExclusive <= fromInclusive`
// (confirmed both the equal and less-than cases); the 3-arg overloads additionally throw (param
// `rangeSize`) when `rangeSize <= 0` and the range itself is otherwise valid — confirmed the
// `fromInclusive`/`toExclusive` check fires FIRST when both are simultaneously invalid (probe-argument
// correctness: an invalid range always wins over an invalid rangeSize in the reported reason). All
// parameters are `int`/`long` — const-capable. No `[SupportedOSPlatform]` gating, no side effects —
// `Partitioner.Create` just builds an object graph, no work is scheduled. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPartitionerCreateArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0512";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Partitioner.Create argument is invalid",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Partitioner.Create(fromInclusive, toExclusive[, rangeSize]) throws ArgumentOutOfRangeException when toExclusive is not greater than fromInclusive, or when rangeSize (3-arg overloads) isn't positive.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Create" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Create",
                IsStatic: true,
                ContainingType: { } containingType,
            } method)
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Collections.Concurrent.Partitioner") return;

        var isLong = method.Parameters is [{ Type.SpecialType: SpecialType.System_Int64 }, ..];
        var isInt = method.Parameters is [{ Type.SpecialType: SpecialType.System_Int32 }, ..];
        if (!isLong && !isInt) return;
        if (method.Parameters.Length is not (2 or 3)) return;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count != method.Parameters.Length) return;

        try
        {
            if (isInt)
            {
                if (context.SemanticModel.GetConstantValue(args[0].Expression, ct).Value is not int from) return;
                if (context.SemanticModel.GetConstantValue(args[1].Expression, ct).Value is not int to) return;
                if (args.Count == 2)
                {
                    _ = Partitioner.Create(from, to);
                }
                else
                {
                    if (context.SemanticModel.GetConstantValue(args[2].Expression, ct).Value is not int rangeSize) return;
                    _ = Partitioner.Create(from, to, rangeSize);
                }
            }
            else
            {
                if (context.SemanticModel.GetConstantValue(args[0].Expression, ct).Value is not long from) return;
                if (context.SemanticModel.GetConstantValue(args[1].Expression, ct).Value is not long to) return;
                if (args.Count == 2)
                {
                    _ = Partitioner.Create(from, to);
                }
                else
                {
                    if (context.SemanticModel.GetConstantValue(args[2].Expression, ct).Value is not long rangeSize) return;
                    _ = Partitioner.Create(from, to, rangeSize);
                }
            }
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            var reason = ex.ParamName == "rangeSize"
                ? "rangeSize must be positive"
                : "toExclusive must be greater than fromInclusive";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), reason));
        }
    }
}
