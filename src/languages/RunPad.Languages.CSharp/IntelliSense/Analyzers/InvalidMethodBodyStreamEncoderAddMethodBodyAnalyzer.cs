using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the twenty-fifth research subagent survey,
// re-verified independently before implementing. `System.Reflection.Metadata.Ecma335.
// MethodBodyStreamEncoder.AddMethodBody(int codeSize, int maxStack, int exceptionRegionCount, ...)` —
// another sibling under the already-covered Ecma335 tree (RPAD0474/0479-0481/0487). Targets only the
// two overloads whose first three parameters are `int`/`int`/`int` (the other two overloads take an
// `InstructionEncoder` as the first parameter and compute codeSize themselves, out of scope). Confirmed
// empirically: `codeSize < 0`, `maxStack` outside `[0, 65535]`, or `exceptionRegionCount < 0` all throw
// `ArgumentOutOfRangeException`. Confirmed a REAL named-argument-omission case (not hypothetical):
// reflection shows the 7-parameter overload defaults everything after `codeSize` (`maxStack` defaults
// to 8, `exceptionRegionCount` to 0, etc.) — verified `AddMethodBody(codeSize: 1, exceptionRegionCount:
// -1)` (skipping the positionally-earlier `maxStack` via a named argument) still throws correctly, so
// this analyzer matches arguments by PARAMETER NAME (via `NameColon` or positional `IMethodSymbol.
// Parameters` lookup), not a fixed array-pattern index — the same fix applied to
// `InvalidSignatureTypeEncoderArgumentAnalyzer`/RPAD0487 and `InvalidPEHeaderBuilderConstructorAnalyzer`/
// RPAD0486 earlier this проход. No `[SupportedOSPlatform]` gating, no side effects — writes into an
// in-memory `BlobBuilder` only. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMethodBodyStreamEncoderAddMethodBodyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0488";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MethodBodyStreamEncoder.AddMethodBody argument is out of range",
        messageFormat: "'MethodBodyStreamEncoder.AddMethodBody(...)' always throws ArgumentOutOfRangeException at runtime — {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MethodBodyStreamEncoder.AddMethodBody throws ArgumentOutOfRangeException when codeSize or exceptionRegionCount is negative, or when maxStack is outside [0, 65535].");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "AddMethodBody" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "AddMethodBody",
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }, ..],
            } method)
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.Ecma335.MethodBodyStreamEncoder") return;

        var args = invocation.ArgumentList.Arguments;
        int? codeSize = FindIntArgument(context.SemanticModel, method, args, "codeSize", ct);
        int? maxStack = FindIntArgument(context.SemanticModel, method, args, "maxStack", ct);
        int? exceptionRegionCount = FindIntArgument(context.SemanticModel, method, args, "exceptionRegionCount", ct);
        if (codeSize is null && maxStack is null && exceptionRegionCount is null) return;

        try
        {
            new MethodBodyStreamEncoder(new BlobBuilder()).AddMethodBody(
                codeSize ?? 0, maxStack ?? 8, exceptionRegionCount ?? 0);
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            var reason = ex.ParamName switch
            {
                "codeSize" when codeSize is not null => "codeSize must be non-negative",
                "maxStack" when maxStack is not null => "maxStack must be between 0 and 65535",
                "exceptionRegionCount" when exceptionRegionCount is not null => "exceptionRegionCount must be non-negative",
                _ => null,
            };
            if (reason is null) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), reason));
        }
    }

    private static int? FindIntArgument(SemanticModel model, IMethodSymbol method, SeparatedSyntaxList<ArgumentSyntax> args, string paramName, System.Threading.CancellationToken ct)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].NameColon?.Name.Identifier.Text ?? (i < method.Parameters.Length ? method.Parameters[i].Name : null);
            if (name != paramName) continue;
            return model.GetConstantValue(args[i].Expression, ct).Value as int?;
        }
        return null;
    }
}
