using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.SequenceReader<T>.Advance(long
// count)`/`.Rewind(long count)` — an OPEN item left by the previous (28th) round's
// `InvalidReadOnlySequenceArgumentAnalyzer` (RPAD0497), which explicitly declined `SequenceReader<T>`
// because it's a `ref struct` and can't be captured in a lambda/used as a generic argument. Resolved
// this round by probing with a direct `try`/`catch` block instead of the usual lambda-based `Check`
// helper (the analyzer's own `Analyze` method already uses direct try/catch, so this only affected the
// scratch verification harness, not the analyzer's production code shape). Confirmed empirically: both
// methods throw `ArgumentOutOfRangeException` (param `count`) for any negative `long` literal,
// independent of reader state (confirmed on both a fresh reader and one that had already consumed
// data); `count == 0` never throws. No `[SupportedOSPlatform]` gating (reflection: only
// `IsByRefLikeAttribute`/compiler-feature attributes). Pure in-memory buffer bookkeeping, no side
// effects. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSequenceReaderArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0499";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SequenceReader argument is negative",
        messageFormat: "'SequenceReader<...>.{0}({1})' always throws ArgumentOutOfRangeException at runtime — count must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SequenceReader<T>.Advance(long)/Rewind(long) throw ArgumentOutOfRangeException for a negative count.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        var isAdvance = methodName == "Advance";
        if (!isAdvance && methodName != "Rewind") return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Buffers.SequenceReader<T>") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var countArg }]) return;
        var constant = context.SemanticModel.GetConstantValue(countArg, ct).Value;
        long count;
        switch (constant)
        {
            case long l: count = l; break;
            case int i: count = i; break;
            default: return;
        }
        if (count >= 0) return;

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[8]));
        try
        {
            if (isAdvance) reader.Advance(count);
            else reader.Rewind(count);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, count));
        }
    }
}
