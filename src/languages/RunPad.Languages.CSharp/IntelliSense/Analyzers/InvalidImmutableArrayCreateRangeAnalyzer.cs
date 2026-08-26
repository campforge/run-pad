using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (456-й проход, batch) — candidate #5 from the eighteenth research subagent survey,
// re-verified independently before implementing. `System.Collections.Immutable.ImmutableArray.
// Create<T>(T[] items, int start, int length)` (and its `ImmutableArray<T>` source overload, same
// shape) — a two-joint-literal analyzer: correctness depends on `start`/`length` TOGETHER against the
// (non-literal) source array's actual runtime length, which this analyzer cannot know statically — so it
// is deliberately scoped to only the two provably-invariant failure modes that hold regardless of the
// source array's length: a negative `start`, or a negative `length`. Confirmed empirically both throw
// `ArgumentOutOfRangeException` (param `start`/`length` respectively) — confirmed via a real 3-element
// array that `start + length` exceeding the array's bound ALSO throws, but that failure mode depends on
// the source array's actual length, which isn't literal-invariant, so it is intentionally NOT checked
// here (same discipline already established for `InvalidGuidByteArrayLengthAnalyzer`/RPAD0266's array-
// length family, and the schema/state-dependence trap this catalog has hit before). Confirmed a
// zero-length slice exactly at the array's end (`Create(arr, arr.Length, 0)`) is valid, not flagged.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidImmutableArrayCreateRangeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0465";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ImmutableArray.Create start/length argument always throws",
        messageFormat: "'ImmutableArray.Create<...>(...)' always throws ArgumentOutOfRangeException at runtime — {0} ({1}) cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ImmutableArray.Create<T>(items, start, length) throws ArgumentOutOfRangeException when start or length is negative.");

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
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var startArg }, { Expression: var lengthArg }])
            return;

        var ct = context.CancellationToken;
        var model = context.SemanticModel;
        var startValue = model.GetConstantValue(startArg, ct).Value as int?;
        var lengthValue = model.GetConstantValue(lengthArg, ct).Value as int?;
        if (startValue is null && lengthValue is null) return;

        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Create",
                IsStatic: true,
                Parameters: [_, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Collections.Immutable.ImmutableArray") return;

        if (startValue is < 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "start", startValue));
        }
        else if (lengthValue is < 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "length", lengthValue));
        }
    }
}
