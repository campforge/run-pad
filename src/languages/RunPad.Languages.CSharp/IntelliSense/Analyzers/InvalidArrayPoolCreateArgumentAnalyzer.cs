using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor/factory argument validation" theme as InvalidBlockingCollectionConstructorArgumentAnalyzer
// (RPAD0333, проход 352): `ArrayPool<T>.Create(int maxArrayLength, int maxArraysPerBucket)` throws
// ArgumentOutOfRangeException when either argument isn't strictly positive — confirmed empirically via
// scratch `dotnet run` script. A static factory method rather than a constructor, but the same "literal
// argument that always throws" shape as the rest of this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArrayPoolCreateArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0341";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ArrayPool<T>.Create arguments are not positive",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — {1} must be positive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ArrayPool<T>.Create(maxArrayLength, maxArraysPerBucket) throws ArgumentOutOfRangeException when either argument isn't strictly positive.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Create" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var maxLengthArg }, { Expression: var maxBucketsArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(maxLengthArg, ct).Value is not int maxArrayLength) return;
        if (context.SemanticModel.GetConstantValue(maxBucketsArg, ct).Value is not int maxArraysPerBucket) return;

        string parameterName;
        if (maxArrayLength < 1)
        {
            parameterName = "maxArrayLength";
        }
        else if (maxArraysPerBucket < 1)
        {
            parameterName = "maxArraysPerBucket";
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Create" } method)
            return;
        if (method.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Buffers.ArrayPool<T>") return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), parameterName));
    }
}
