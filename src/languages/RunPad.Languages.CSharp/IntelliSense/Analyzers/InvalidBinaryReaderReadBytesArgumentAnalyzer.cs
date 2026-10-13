using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the twenty-ninth research subagent survey,
// re-verified independently before implementing. `System.IO.BinaryReader.ReadBytes(int count)` —
// first touch of `BinaryReader`. Confirmed empirically: a negative `count` always throws
// `ArgumentOutOfRangeException` (param `count`); `count == 0` is valid (returns an empty array).
// Confirmed genuinely distinct from the existing `InvalidStreamReadOrWriteArgumentAnalyzer` (RPAD0336),
// which is scoped strictly to `MemoryStream.Read/Write(byte[], int, int)` — an array-bounds-dependent,
// three-argument shape, not this single-`int`-parameter, array-independent method (confirmed by
// reading that analyzer's doc comment before implementing). No `[SupportedOSPlatform]` gating, no side
// effects — probing against a fresh in-memory `MemoryStream`-backed reader never touches disk/network.
// Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBinaryReaderReadBytesArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0501";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BinaryReader.ReadBytes count is negative",
        messageFormat: "'BinaryReader.ReadBytes({0})' always throws ArgumentOutOfRangeException at runtime — count must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BinaryReader.ReadBytes(int count) throws ArgumentOutOfRangeException for a negative count.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ReadBytes" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "ReadBytes",
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.IO.BinaryReader") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var countArg }]) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;
        if (count >= 0) return;

        using var ms = new MemoryStream();
        using var reader = new BinaryReader(ms);
        try
        {
            reader.ReadBytes(count);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), count));
        }
    }
}
