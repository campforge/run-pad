using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Sibling to
// PreferNewStringOverJoinOnCharArrayAnalyzer (RPAD0186, проход 198), which covers the SAME
// `string.Join("", xs)` shape for a char sequence — this one covers it for a STRING sequence instead
// (mutually exclusive by element type, so the two never fire on the same call). `string.Join("", xs)`
// runs every element through the separator-insertion machinery just to insert an empty separator
// between them — `string.Concat(xs)` does the same concatenation directly, without ever considering a
// separator. Verified via PROBE that this doesn't collide with any built-in IDE rule.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferStringConcatOverJoinWithEmptySeparatorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0216";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer string.Concat over string.Join with an empty separator",
        messageFormat: "'string.Join(\"\", xs)' can be replaced with 'string.Concat(xs)' — same result, without the separator-insertion logic",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "string.Join(\"\", xs) inserts an empty separator between every element — string.Concat(xs) concatenates directly without considering a separator at all.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Join" })
            return;
        if (invocation.ArgumentList.Arguments is not [var sepArg, _])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Join" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        var separatorConstant = context.SemanticModel.GetConstantValue(sepArg.Expression, ct);
        if (separatorConstant.Value is not string { Length: 0 }) return;

        ITypeSymbol? elementType = method switch
        {
            { IsGenericMethod: true, TypeArguments: [var typeArgument] } => typeArgument,
            { Parameters: [_, { Type: IArrayTypeSymbol arrayType }] } => arrayType.ElementType,
            { Parameters: [_, { Type: INamedTypeSymbol { TypeArguments: [var namedElementType] } }] } => namedElementType,
            _ => null,
        };
        if (elementType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
