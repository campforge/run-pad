using System.Collections.Immutable;
using System.Net.Mime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; one of the four
// lower-confidence "needs verification" candidates from the research subagent's ranked list (проход 377),
// confirmed empirically before implementing (all four confirmed real via one combined scratch script, this
// проход implements the first/cleanest of the four). Sibling of InvalidContentTypeLiteralAnalyzer (RPAD0373,
// проход 380) — same "run the real validator via try/catch around the real constructor" theme, applied to
// `new System.Net.Mime.ContentDisposition(string)` instead of `ContentType`. Genuinely NOT covered by the
// pre-existing InvalidHttpHeaderValueLiteralAnalyzer (RPAD0371, проход 378): that analyzer's
// "ContentDispositionHeaderValue" case is `System.Net.Http.Headers.ContentDispositionHeaderValue`, a
// different type in a different namespace — same split as ContentType vs MediaTypeHeaderValue already
// established in this catalog. Confirmed empirically this is a single-exception-type situation (unlike its
// ContentType sibling): every failure mode (empty string, whitespace-only, unbalanced parameter syntax like
// ";;;", a parameter with no value like "filename=") throws plain `FormatException`, never a more specific
// subtype. Also confirmed the constructor is lenient about the disposition-type token itself — ANY
// single token with no semicolons is accepted as a valid (if unrecognized) disposition-type, same leniency
// already observed for ContentType's own type/subtype tokens — so this analyzer only catches genuinely
// malformed parameter syntax, not "unrecognized disposition-type" (there's no such concept to validate).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidContentDispositionLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0375";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ContentDisposition literal is not a valid content disposition string",
        messageFormat: "'new ContentDisposition(...)' always throws FormatException at runtime — '{0}' is not a valid content disposition string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The System.Net.Mime.ContentDisposition(string) constructor throws FormatException for a string with malformed parameter syntax.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var dispositionArg }] }) return;
        if (context.SemanticModel.GetConstantValue(dispositionArg, ct).Value is not string disposition) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
            } method)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Net.Mime.ContentDisposition") return;

        try
        {
            _ = new ContentDisposition(disposition);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), disposition));
        }
    }
}
