using System.Collections.Immutable;
using System.Net.Cache;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Net.Cache.HttpRequestCachePolicy(
// HttpRequestCacheLevel)`/`RequestCachePolicy(RequestCacheLevel)` constructors — an out-of-range enum
// value throws `ArgumentOutOfRangeException`. Confirmed empirically both types are pure managed
// value-holders with ZERO side effects at construction time — no actual HTTP/disk cache is touched until
// the policy is later attached to a live `WebRequest`, which construction alone never does. Confirmed
// invariant across en-US/de-DE/ar-SA/ja-JP. Scoped to a literal numeric value cast to the target enum
// type, same shape already established by `InvalidDateTimeFormatInfoEnumPropertyAnalyzer` (RPAD0442).
// Both types dispatched from one analyzer since they share the identical (enum-cast constructor argument)
// shape and exception type, differing only in which enum/type name.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCachePolicyConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0451";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Cache policy constructor argument is out of range",
        messageFormat: "'new {0}((...){1})' always throws ArgumentOutOfRangeException at runtime — value is outside the valid range for {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "HttpRequestCachePolicy(HttpRequestCacheLevel) and RequestCachePolicy(RequestCacheLevel) throw ArgumentOutOfRangeException for a value outside their own enum's defined range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var levelArg }] }) return;
        if (levelArg is not CastExpressionSyntax { Expression: var innerExpr }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type: { } enumType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        var typeName = containingType.ToDisplayString();
        var enumTypeName = enumType.ToDisplayString();

        object? thrown;
        try
        {
            thrown = typeName switch
            {
                "System.Net.Cache.HttpRequestCachePolicy" when enumTypeName == "System.Net.Cache.HttpRequestCacheLevel" =>
                    new HttpRequestCachePolicy((HttpRequestCacheLevel)rawValue),
                "System.Net.Cache.RequestCachePolicy" when enumTypeName == "System.Net.Cache.RequestCacheLevel" =>
                    new RequestCachePolicy((RequestCacheLevel)rawValue),
                _ => null,
            };
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), containingType.Name, rawValue, enumType.Name));
            return;
        }
        _ = thrown;
    }
}
