using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.ArrayBufferWriter<T>` — constructor
// plus three instance methods, bundled under one diagnostic (precedent RPAD0426). Confirmed
// empirically: `ArrayBufferWriter<T>(int initialCapacity)` throws `ArgumentException` for
// `initialCapacity <= 0` (both 0 and negative throw — NOT just negative); `.GetSpan(int sizeHint)`/
// `.GetMemory(int sizeHint)` throw `ArgumentException` for a negative `sizeHint`; `.Advance(int count)`
// throws `ArgumentException` for a negative `count`. The generic type argument doesn't affect any
// check (confirmed with `byte`). No `[SupportedOSPlatform]` gating (reflection: no attributes on the
// type), no side effects — pure in-memory array allocation/bookkeeping. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArrayBufferWriterArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0494";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ArrayBufferWriter argument is invalid",
        messageFormat: "'{0}' always throws ArgumentException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ArrayBufferWriter<T>'s constructor throws ArgumentException for a non-positive initialCapacity, and GetSpan/GetMemory/Advance throw ArgumentException for a negative argument.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Buffers.ArrayBufferWriter<T>") return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var capacityArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(capacityArg, ct).Value is not int initialCapacity) return;
        if (initialCapacity > 0) return;

        try
        {
            _ = new ArrayBufferWriter<byte>(initialCapacity);
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString(), "initialCapacity must be positive"));
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        if (methodName is not ("GetSpan" or "GetMemory" or "Advance")) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.OriginalDefinition.ToDisplayString() != "System.Buffers.ArrayBufferWriter<T>") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var argExpr }]) return;
        if (context.SemanticModel.GetConstantValue(argExpr, ct).Value is not int value) return;
        if (value >= 0) return;

        var writer = new ArrayBufferWriter<byte>();
        try
        {
            switch (methodName)
            {
                case "GetSpan": _ = writer.GetSpan(value); break;
                case "GetMemory": _ = writer.GetMemory(value); break;
                default: writer.Advance(value); break;
            }
        }
        catch (System.ArgumentException)
        {
            var paramName = methodName == "Advance" ? "count" : "sizeHint";
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), $"{paramName} must be non-negative"));
        }
    }
}
