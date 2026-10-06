using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (465-й проход, batch) — candidate #2 from the twenty-eighth research subagent survey,
// re-verified independently before implementing. `System.Buffers.ReadOnlySequence<T>`'s constructor
// and `.Slice(int, int)` method — bundled under one diagnostic (precedent RPAD0426). Confirmed
// empirically: `new ReadOnlySequence<T>(T[] array, int start, int length)` throws
// `ArgumentOutOfRangeException` for a negative `start` or negative `length`, independent of the
// array's actual length (checked before bounds validation); `.Slice(int start, int length)` throws the
// same way for the same reason. `SequenceReader<T>.Advance(long)`/`.Rewind(long)` were also confirmed
// to throw `ArgumentOutOfRangeException` for a negative argument regardless of reader state — but
// `SequenceReader<T>` is a `ref struct`, which cannot be a type argument, field, or captured in a
// lambda, making it structurally awkward to probe/report through the same call-real-API pattern as
// everything else in this catalog; declined for THIS analyzer to keep scope manageable, covering only
// the two non-ref-struct `ReadOnlySequence<T>` call shapes. No `[SupportedOSPlatform]` gating, no side
// effects — pure in-memory buffer bookkeeping. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidReadOnlySequenceArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0497";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ReadOnlySequence argument is negative",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — {1} must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ReadOnlySequence<T>'s constructor and Slice(int, int) throw ArgumentOutOfRangeException for a negative start or length.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeSlice, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
                Parameters: [{ Type: IArrayTypeSymbol }, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Buffers.ReadOnlySequence<T>") return;
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var startArg }, { Expression: var lengthArg }] })
            return;

        var ct = context.CancellationToken;
        var start = context.SemanticModel.GetConstantValue(startArg, ct).Value as int?;
        var length = context.SemanticModel.GetConstantValue(lengthArg, ct).Value as int?;
        if (start is null && length is null) return;
        if (start is >= 0 && length is >= 0) return;

        try
        {
            _ = new ReadOnlySequence<byte>(new byte[8], start ?? 0, length ?? 0);
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            if (ex.ParamName == "start" && start is null) return;
            if (ex.ParamName == "length" && length is null) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString(), ex.ParamName));
        }
    }

    private static void AnalyzeSlice(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Slice" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Slice",
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Buffers.ReadOnlySequence<T>") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var startArg }, { Expression: var lengthArg }])
            return;

        var start = context.SemanticModel.GetConstantValue(startArg, ct).Value as int?;
        var length = context.SemanticModel.GetConstantValue(lengthArg, ct).Value as int?;
        if (start is null && length is null) return;
        if (start is >= 0 && length is >= 0) return;

        try
        {
            _ = new ReadOnlySequence<byte>(new byte[8]).Slice(start ?? 0, length ?? 0);
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            if (ex.ParamName == "start" && start is null) return;
            if (ex.ParamName == "length" && length is null) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), ex.ParamName));
        }
    }
}
