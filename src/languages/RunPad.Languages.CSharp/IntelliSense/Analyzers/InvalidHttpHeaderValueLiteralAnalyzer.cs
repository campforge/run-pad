using System.Collections.Immutable;
using System.Net.Http.Headers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Every member below documents `FormatException` for malformed input in the .NET SDK
// reference-assembly XML doc comments, confirmed empirically for 8 of the 19 types.
// `System.Net.Http.Headers` has a large family of `XxxHeaderValue.Parse(string)` static methods — one
// per HTTP header value shape (media type, authentication challenge, cache-control directive, entity
// tag, product token, byte-range, etc.) — that all throw `FormatException` for a string that doesn't
// match that header's own grammar, all with the exact same message shape ("The format of value '...' is
// invalid."), confirmed identical across every sampled type. One analyzer covers all 19 via a
// `ContainingType`-keyed dispatch to each type's own real `TryParse`, matching this catalog's established
// multi-type-dispatch precedent (e.g. RPAD0356 for the 14 numeric Parse types) rather than 19 separate
// near-identical analyzer classes.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHttpHeaderValueLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0371";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "HTTP header value Parse argument does not match that header's grammar",
        messageFormat: "'{0}.Parse(...)' always throws FormatException at runtime — '{1}' is not a valid {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The System.Net.Http.Headers XxxHeaderValue.Parse(string) family throws FormatException when the literal doesn't match that header value's own grammar.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetTypeNames = ImmutableHashSet.Create(
        "AuthenticationHeaderValue", "CacheControlHeaderValue", "ContentDispositionHeaderValue",
        "ContentRangeHeaderValue", "EntityTagHeaderValue", "MediaTypeHeaderValue",
        "MediaTypeWithQualityHeaderValue", "NameValueHeaderValue", "NameValueWithParametersHeaderValue",
        "ProductHeaderValue", "ProductInfoHeaderValue", "RangeConditionHeaderValue", "RangeHeaderValue",
        "RetryConditionHeaderValue", "StringWithQualityHeaderValue", "TransferCodingHeaderValue",
        "TransferCodingWithQualityHeaderValue", "ViaHeaderValue", "WarningHeaderValue");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse", ContainingType: { } containingType })
            return;
        if (containingType.ContainingNamespace?.ToDisplayString() != "System.Net.Http.Headers") return;
        var typeName = containingType.Name;
        if (!TargetTypeNames.Contains(typeName)) return;

        if (!FailsToParse(typeName, value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), typeName, value));
    }

    private static bool FailsToParse(string typeName, string value) => typeName switch
    {
        "AuthenticationHeaderValue" => !AuthenticationHeaderValue.TryParse(value, out _),
        "CacheControlHeaderValue" => !CacheControlHeaderValue.TryParse(value, out _),
        "ContentDispositionHeaderValue" => !ContentDispositionHeaderValue.TryParse(value, out _),
        "ContentRangeHeaderValue" => !ContentRangeHeaderValue.TryParse(value, out _),
        "EntityTagHeaderValue" => !EntityTagHeaderValue.TryParse(value, out _),
        "MediaTypeHeaderValue" => !MediaTypeHeaderValue.TryParse(value, out _),
        "MediaTypeWithQualityHeaderValue" => !MediaTypeWithQualityHeaderValue.TryParse(value, out _),
        "NameValueHeaderValue" => !NameValueHeaderValue.TryParse(value, out _),
        "NameValueWithParametersHeaderValue" => !NameValueWithParametersHeaderValue.TryParse(value, out _),
        "ProductHeaderValue" => !ProductHeaderValue.TryParse(value, out _),
        "ProductInfoHeaderValue" => !ProductInfoHeaderValue.TryParse(value, out _),
        "RangeConditionHeaderValue" => !RangeConditionHeaderValue.TryParse(value, out _),
        "RangeHeaderValue" => !RangeHeaderValue.TryParse(value, out _),
        "RetryConditionHeaderValue" => !RetryConditionHeaderValue.TryParse(value, out _),
        "StringWithQualityHeaderValue" => !StringWithQualityHeaderValue.TryParse(value, out _),
        "TransferCodingHeaderValue" => !TransferCodingHeaderValue.TryParse(value, out _),
        "TransferCodingWithQualityHeaderValue" => !TransferCodingWithQualityHeaderValue.TryParse(value, out _),
        "ViaHeaderValue" => !ViaHeaderValue.TryParse(value, out _),
        "WarningHeaderValue" => !WarningHeaderValue.TryParse(value, out _),
        _ => false,
    };
}
