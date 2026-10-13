using System.Collections.Immutable;
using System.Net.Mime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the real validator via try/catch around the real constructor" theme as
// InvalidCultureNameLiteralAnalyzer/InvalidRegionInfoLiteralAnalyzer (RPAD0248/RPAD0372): `new System.Net.
// Mime.ContentType(string)` throws for a string that isn't a valid MIME content-type ("type/subtype[;
// parameters]"). Confirmed empirically that this is genuinely a TWO-exception-type situation, not one:
// an EMPTY string throws `ArgumentException` specifically, while every OTHER malformed shape (no slash,
// missing subtype, whitespace-only) throws `FormatException` instead — the message format parameterizes
// the exception type name rather than hardcoding one, same technique RPAD0357/RPAD0363 already use for
// their own multi-exception-type situations in this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidContentTypeLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0373";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ContentType literal is not a valid MIME content type",
        messageFormat: "'new ContentType(...)' always throws {0} at runtime — '{1}' is not a valid MIME content type",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The System.Net.Mime.ContentType(string) constructor throws ArgumentException for an empty string, or FormatException for any other string that isn't a valid \"type/subtype\" MIME content type.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var contentTypeArg }, ..] }) return;
        if (context.SemanticModel.GetConstantValue(contentTypeArg, ct).Value is not string contentType) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Net.Mime.ContentType") return;

        try
        {
            _ = new ContentType(contentType);
        }
        catch (System.Exception ex) when (ex is System.FormatException or System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), ex.GetType().Name, contentType));
        }
    }
}
