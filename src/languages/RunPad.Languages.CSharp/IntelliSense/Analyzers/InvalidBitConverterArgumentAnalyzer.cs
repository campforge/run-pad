using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести семьдесят девятый проход, batch) — own pick, not a Roslynator port. A correctness
// finding: `BitConverter.ToInt32(byte[] value, int startIndex)` (and the other ToXxx(byte[], int)
// overloads — ToBoolean/ToChar/ToInt16/ToUInt16/ToInt32/ToUInt32/ToInt64/ToUInt64/ToSingle/ToDouble,
// each needing a different byte count) always throws when `startIndex` and the array's length don't
// leave enough bytes to read. Confirmed empirically before writing this exactly which exception each
// case throws — the boundary logic turned out to have two distinct branches, not one: `startIndex < 0`
// OR `startIndex >= array.Length` (the index itself isn't a valid position in the array) throws
// `ArgumentOutOfRangeException`, while a startIndex that IS a valid position but doesn't leave enough
// trailing bytes for the target type throws `ArgumentException` instead — e.g.
// `BitConverter.ToInt32(new byte[4], 4)` (index itself invalid) throws ArgumentOutOfRangeException, but
// `BitConverter.ToInt32(new byte[4], 3)` (valid index, only 1 byte left of 4 needed) throws
// ArgumentException. Scoped to a LITERAL array (known length, same helper as
// InvalidGuidByteArrayLengthAnalyzer/RPAD0266) and a literal (constant) `startIndex`.
// Extended in the триста седьмой проход to also cover the 3-argument `BitConverter.ToString(byte[]
// value, int startIndex, int length)` — a genuinely different method (not in the ToXxx family above,
// takes an explicit `length` instead of an implied fixed byte count) with a SUBTLER boundary than the
// two-argument family: confirmed empirically (scratch `dotnet run` script) that `startIndex ==
// array.Length` is fine when the array is EMPTY (both zero) but throws `ArgumentOutOfRangeException` for
// any NON-empty array — rather than hand-coding that edge case, this path constructs a same-LENGTH
// placeholder `byte[]` (content is irrelevant — the boundary check only depends on length) and calls the
// REAL `BitConverter.ToString` as the validator, same "run the real validator" technique used elsewhere
// in this catalog, avoiding the risk of getting the edge case wrong by hand.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBitConverterArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0268";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BitConverter.ToXxx argument does not leave enough bytes to read",
        messageFormat: "'BitConverter.{0}(...)' always throws {1} at runtime — a {2}-byte array starting at index {3} doesn't leave {4} bytes to read",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BitConverter.ToXxx(byte[], int) throws ArgumentOutOfRangeException when startIndex isn't a valid position in the array, or ArgumentException when the array doesn't have enough bytes left from startIndex for the target type.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, int> RequiredByteCountByMethod = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create("ToBoolean", 1),
            KeyValuePair.Create("ToChar", 2),
            KeyValuePair.Create("ToInt16", 2),
            KeyValuePair.Create("ToUInt16", 2),
            KeyValuePair.Create("ToInt32", 4),
            KeyValuePair.Create("ToUInt32", 4),
            KeyValuePair.Create("ToSingle", 4),
            KeyValuePair.Create("ToInt64", 8),
            KeyValuePair.Create("ToUInt64", 8),
            KeyValuePair.Create("ToDouble", 8),
        ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeToString, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeToString(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arrayArg }, { Expression: var startIndexArg }, { Expression: var lengthArg }])
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(startIndexArg, ct).Value is not int startIndex) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToString" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.BitConverter") return;

        var exceptionType = ToStringAlwaysThrows(arrayLength.Value, startIndex, length);
        if (exceptionType is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "ToString", exceptionType, arrayLength.Value, startIndex, length));
    }

    private static string? ToStringAlwaysThrows(int arrayLength, int startIndex, int length)
    {
        try
        {
            _ = BitConverter.ToString(new byte[arrayLength], startIndex, length);
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return "ArgumentOutOfRangeException";
        }
        catch (ArgumentException)
        {
            return "ArgumentException";
        }
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        if (!RequiredByteCountByMethod.TryGetValue(methodName, out var requiredBytes)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arrayArg }, { Expression: var startIndexArg }])
            return;

        var ct = context.CancellationToken;
        var arrayLength = TryGetArrayLength(arrayArg, context.SemanticModel, ct);
        if (arrayLength is null) return;
        if (context.SemanticModel.GetConstantValue(startIndexArg, ct).Value is not int startIndex) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.BitConverter") return;

        string exceptionType;
        if (startIndex < 0 || startIndex >= arrayLength)
        {
            exceptionType = "ArgumentOutOfRangeException";
        }
        else if (arrayLength - startIndex < requiredBytes)
        {
            exceptionType = "ArgumentException";
        }
        else
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, exceptionType, arrayLength, startIndex, requiredBytes));
    }

    private static int? TryGetArrayLength(ExpressionSyntax expression, SemanticModel semanticModel, System.Threading.CancellationToken ct)
    {
        if (expression is ArrayCreationExpressionSyntax arrayCreation)
        {
            if (arrayCreation.Initializer is { } init) return init.Expressions.Count;
            if (arrayCreation.Type.RankSpecifiers is [{ Sizes: [var sizeExpr] }, ..]
                && semanticModel.GetConstantValue(sizeExpr, ct).Value is int size)
            {
                return size;
            }
            return null;
        }
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
            return implicitInit.Expressions.Count;
        if (expression is CollectionExpressionSyntax collectionExpr)
            return collectionExpr.Elements.Count;
        return null;
    }
}
