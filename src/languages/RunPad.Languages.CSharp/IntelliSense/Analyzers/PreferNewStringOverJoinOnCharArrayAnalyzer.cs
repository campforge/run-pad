using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `string.Join("", charArray)` binds to the generic `string.Join<T>(string, IEnumerable<T>)` overload
// (there is no `Join(string, char[])` overload), which enumerates the array and calls `ToString()` on
// every `char` through a StringBuilder-like accumulator — the `string(char[])` constructor copies the
// same characters into the new string directly, without per-element `ToString()` calls.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferNewStringOverJoinOnCharArrayAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0186";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the string(char[]) constructor over Join on an empty separator",
        messageFormat: "'string.Join(\"\", charArray)' enumerates the array and calls ToString() on every char — use 'new string(charArray)' to copy the characters directly",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "string.Join(\"\", charArray) has no dedicated char[] overload, so it falls back to the generic IEnumerable<T> overload and calls ToString() per element — the string(char[]) constructor copies the buffer directly.");

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
        if (invocation.ArgumentList.Arguments is not [var sepArg, var valuesArg])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Join" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        var separatorConstant = context.SemanticModel.GetConstantValue(sepArg.Expression, ct);
        if (separatorConstant.Value is not string { Length: 0 }) return;

        var valuesType = context.SemanticModel.GetTypeInfo(valuesArg.Expression, ct).Type;
        if (valuesType is not IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Char })
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
