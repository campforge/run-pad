using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Reflection.Metadata.Ecma335.
// SignatureTypeEncoder` — another sibling under the already-covered `BlobBuilder`/`MetadataBuilder`/
// `InstructionEncoder` namespace (RPAD0474/0479-0481). Three call shapes, bundled under one diagnostic
// (precedent RPAD0426): `GenericTypeParameter(int parameterIndex)`/`GenericMethodTypeParameter(int
// parameterIndex)` — valid range is exactly `[0, 65535]`, confirmed at both boundaries (65535 valid,
// 65536 throws; -1/int.MinValue throw); `FunctionPointer(SignatureCallingConvention,
// FunctionPointerAttributes, int genericParameterCount)` — the same `[0, 65535]` bound on
// `genericParameterCount`; `GenericInstantiation(EntityHandle, int genericArgumentCount, bool)` —
// `genericArgumentCount` must be `>= 1` (0 and -1 both throw, confirmed the count check fires BEFORE
// the handle is inspected, so the handle argument doesn't need to be a literal for this diagnostic to
// apply). Confirmed no `[SupportedOSPlatform]` gating, no side effects — pure signature-blob
// construction into a throwaway `BlobBuilder`. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSignatureTypeEncoderArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0487";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SignatureTypeEncoder method argument is out of range",
        messageFormat: "'SignatureTypeEncoder.{0}(...)' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SignatureTypeEncoder.GenericTypeParameter/GenericMethodTypeParameter/FunctionPointer throw ArgumentOutOfRangeException for a parameter/generic count outside [0, 65535], and GenericInstantiation throws when genericArgumentCount is less than 1.");

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
        if (methodName is not ("GenericTypeParameter" or "GenericMethodTypeParameter" or "FunctionPointer" or "GenericInstantiation"))
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.Ecma335.SignatureTypeEncoder") return;

        var args = invocation.ArgumentList.Arguments;
        var targetParamName = methodName switch
        {
            "FunctionPointer" => "genericParameterCount",
            "GenericInstantiation" => "genericArgumentCount",
            _ => "parameterIndex",
        };

        ExpressionSyntax? valueArg = null;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].NameColon?.Name.Identifier.Text ?? (i < method.Parameters.Length ? method.Parameters[i].Name : null);
            if (name == targetParamName)
            {
                valueArg = args[i].Expression;
                break;
            }
        }
        if (valueArg is null) return;
        if (context.SemanticModel.GetConstantValue(valueArg, ct).Value is not int value) return;

        var blob = new BlobBuilder();
        new BlobEncoder(blob).MethodSignature().Parameters(1, out _, out var parametersEncoder);
        var encoder = parametersEncoder.AddParameter().Type();

        try
        {
            switch (methodName)
            {
                case "GenericTypeParameter": encoder.GenericTypeParameter(value); break;
                case "GenericMethodTypeParameter": encoder.GenericMethodTypeParameter(value); break;
                case "FunctionPointer": encoder.FunctionPointer(SignatureCallingConvention.Default, FunctionPointerAttributes.None, value); break;
                default: encoder.GenericInstantiation(MetadataTokens.EntityHandle(0x02000001), value, false); break;
            }
        }
        catch (System.ArgumentOutOfRangeException)
        {
            var reason = methodName == "GenericInstantiation"
                ? "genericArgumentCount must be at least 1"
                : "the value must be between 0 and 65535";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, reason));
        }
    }
}
