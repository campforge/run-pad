using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #3 from the twenty-third research subagent survey,
// re-verified independently before implementing. `System.Reflection.Metadata.Ecma335.
// InstructionEncoder` — another sibling under the already-covered `BlobBuilder`'s namespace (RPAD0474).
// Six instance methods, bundled under one diagnostic (several call shapes — one diagnostic, precedent
// RPAD0426): `LoadLocal`/`StoreLocal`/`LoadLocalAddress(int slotIndex)` and `LoadArgument`/
// `StoreArgument`/`LoadArgumentAddress(int argumentIndex)` — a negative index throws
// `ArgumentOutOfRangeException` for all six, confirmed on a freshly-constructed `new
// InstructionEncoder(new BlobBuilder())`. Confirmed no `[SupportedOSPlatform]` gating and no side
// effects — pure IL-encoding buffer writes, no assembly ever emitted/loaded. Confirmed invariant across
// en-US/de-DE/ar-SA/ja-JP (pure numeric checks).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidInstructionEncoderMethodArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0481";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "InstructionEncoder method argument is negative",
        messageFormat: "'InstructionEncoder.{0}({1})' always throws ArgumentOutOfRangeException at runtime — the index must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "InstructionEncoder.LoadLocal/StoreLocal/LoadLocalAddress/LoadArgument/StoreArgument/LoadArgumentAddress throw ArgumentOutOfRangeException for a negative index.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> MethodNames = ImmutableHashSet.Create(
        "LoadLocal", "StoreLocal", "LoadLocalAddress", "LoadArgument", "StoreArgument", "LoadArgumentAddress");

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
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.Ecma335.InstructionEncoder") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var indexArg }]) return;
        if (context.SemanticModel.GetConstantValue(indexArg, ct).Value is not int index) return;

        var encoder = new InstructionEncoder(new BlobBuilder());
        try
        {
            switch (methodName)
            {
                case "LoadLocal": encoder.LoadLocal(index); break;
                case "StoreLocal": encoder.StoreLocal(index); break;
                case "LoadLocalAddress": encoder.LoadLocalAddress(index); break;
                case "LoadArgument": encoder.LoadArgument(index); break;
                case "StoreArgument": encoder.StoreArgument(index); break;
                default: encoder.LoadArgumentAddress(index); break;
            }
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, index));
        }
    }
}
