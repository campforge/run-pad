using System.Collections.Immutable;
using System.Net.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379/RPAD0380 — grep-confirmed no prior coverage of
// System.Net.Http.HttpMethod before implementing (RPAD0371's HttpHeaderValue family covers 19 different
// XxxHeaderValue.Parse types, none of them HttpMethod). Same "run the real validator via try/catch around
// the real constructor" theme, applied to `new System.Net.Http.HttpMethod(string)` (its only constructor,
// confirmed via reflection). Confirmed empirically this is genuinely a TWO-exception-type situation, same
// class of surprise as InvalidContentTypeLiteralAnalyzer (RPAD0373): an empty or whitespace-only string
// throws `ArgumentException` specifically, while any other malformed HTTP token (containing a space, tab,
// slash, colon, parentheses, quote, or line break — anything outside the HTTP "token" grammar from RFC
// 7230) throws `FormatException` instead — the message format parameterizes the exception type name rather
// than hardcoding one, same technique this catalog's other multi-exception-type analyzers already use.
// Confirmed empirically the constructor does NOT check against a known-method allowlist — any single valid
// HTTP token ("CUSTOM", lowercase "get", etc.) is accepted without throwing, matching the real protocol
// (arbitrary extension methods are legal HTTP).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHttpMethodLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0381";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "HttpMethod literal is not a valid HTTP method token",
        messageFormat: "'new HttpMethod(...)' always throws {0} at runtime — '{1}' is not a valid HTTP method token",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The System.Net.Http.HttpMethod(string) constructor throws ArgumentException for an empty/whitespace-only string, or FormatException for any other string that isn't a valid HTTP token.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ct = context.CancellationToken;
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var methodArg }] }) return;
        if (context.SemanticModel.GetConstantValue(methodArg, ct).Value is not string method) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctorSymbol)
            return;
        if (ctorSymbol.ContainingType?.ToDisplayString() != "System.Net.Http.HttpMethod") return;

        try
        {
            _ = new HttpMethod(method);
        }
        catch (System.Exception ex) when (ex is System.FormatException or System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), ex.GetType().Name, method));
        }
    }
}
