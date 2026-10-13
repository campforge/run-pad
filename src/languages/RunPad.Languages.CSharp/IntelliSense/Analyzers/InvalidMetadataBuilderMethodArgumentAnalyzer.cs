using System.Collections.Immutable;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Reflection.Metadata.Ecma335.MetadataBuilder`
// instance methods, bundled under one diagnostic (several call shapes — one diagnostic, precedent
// RPAD0426): `SetCapacity(TableIndex table, int rowCount)` — negative `rowCount` or `table` outside the
// defined 0-53 range throws `ArgumentOutOfRangeException` (confirmed `TableIndex` is BYTE-backed via
// `Enum.GetUnderlyingType`, boundary at 56/255 both throw); `GetRowCount(TableIndex table)` — same
// `table`-range check; `SetCapacity(HeapIndex heap, int byteCount)` — negative `byteCount` or `heap`
// outside the defined 0-3 range throws (confirmed `HeapIndex` is INT-backed, unlike `TableIndex` —
// checked both underlying types independently rather than assuming); `ReserveUserString(int length)` —
// negative `length` throws. All confirmed on a freshly-constructed `new MetadataBuilder()`. Confirmed
// no `[SupportedOSPlatform]` gating and no side effects — pure in-memory table/heap bookkeeping.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMetadataBuilderMethodArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0480";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MetadataBuilder method argument always throws",
        messageFormat: "'MetadataBuilder.{0}(...)' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MetadataBuilder.SetCapacity/GetRowCount/ReserveUserString throw ArgumentOutOfRangeException for a negative count/length or an undefined TableIndex/HeapIndex value.");

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
        if (methodName is not ("SetCapacity" or "GetRowCount" or "ReserveUserString")) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.Ecma335.MetadataBuilder") return;

        var builder = new MetadataBuilder();
        var args = invocation.ArgumentList.Arguments;

        try
        {
            switch (methodName)
            {
                case "SetCapacity" when args.Count == 2
                    && method.Parameters is [{ Type.Name: "TableIndex" }, _]:
                    {
                        if (context.SemanticModel.GetConstantValue(args[0].Expression, ct) is not { HasValue: true, Value: byte tableRaw }) return;
                        if (context.SemanticModel.GetConstantValue(args[1].Expression, ct) is not { HasValue: true, Value: int rowCount }) return;
                        builder.SetCapacity((TableIndex)tableRaw, rowCount);
                        break;
                    }
                case "SetCapacity" when args.Count == 2
                    && method.Parameters is [{ Type.Name: "HeapIndex" }, _]:
                    {
                        if (context.SemanticModel.GetConstantValue(args[0].Expression, ct) is not { HasValue: true, Value: int heapRaw }) return;
                        if (context.SemanticModel.GetConstantValue(args[1].Expression, ct) is not { HasValue: true, Value: int byteCount }) return;
                        builder.SetCapacity((HeapIndex)heapRaw, byteCount);
                        break;
                    }
                case "GetRowCount" when args.Count == 1:
                    {
                        if (context.SemanticModel.GetConstantValue(args[0].Expression, ct) is not { HasValue: true, Value: byte tableRaw }) return;
                        builder.GetRowCount((TableIndex)tableRaw);
                        break;
                    }
                case "ReserveUserString" when args.Count == 1:
                    {
                        if (context.SemanticModel.GetConstantValue(args[0].Expression, ct) is not { HasValue: true, Value: int length }) return;
                        builder.ReserveUserString(length);
                        break;
                    }
                default:
                    return;
            }
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            var reason = ex.ParamName switch
            {
                "table" => "the value is not a defined TableIndex member",
                "heap" => "the value is not a defined HeapIndex member",
                "rowCount" or "byteCount" or "length" => "the value must be non-negative",
                _ => "the value is out of range",
            };
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, reason));
        }
    }
}
