using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (465-й проход, batch) — candidate #3 from the twenty-eighth research subagent survey,
// re-verified independently before implementing. `System.Reflection.Metadata.Ecma335.
// VectorEncoder.Count(int count)` — another sibling under the already-covered Ecma335 tree
// (RPAD0474/0479-0481/0487/0488). Confirmed empirically: negative `count` always throws
// `ArgumentOutOfRangeException`; `0` is valid (represents an empty fixed-size array signature).
// Sibling encoders `LocalVariablesEncoder`/`NamedArgumentsEncoder`/`LiteralEncoder`/
// `PermissionSetEncoder`/`ScalarEncoder` were checked and declined — their constructors only take a
// `BlobBuilder`, no literal-drivable validating members found. No `[SupportedOSPlatform]` gating, no
// side effects — writes into an in-memory `BlobBuilder` only. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidVectorEncoderCountAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0498";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "VectorEncoder.Count argument is negative",
        messageFormat: "'VectorEncoder.Count({0})' always throws ArgumentOutOfRangeException at runtime — count must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "VectorEncoder.Count(int) throws ArgumentOutOfRangeException for a negative count.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Count",
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.Ecma335.VectorEncoder") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var countArg }]) return;
        if (context.SemanticModel.GetConstantValue(countArg, ct).Value is not int count) return;
        if (count >= 0) return;

        try
        {
            new VectorEncoder(new BlobBuilder()).Count(count);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), count));
        }
    }
}
