using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `new UriBuilder(string)` (1-arg constructor only) — same exception
// family as the already-covered `new Uri(string)` (`InvalidUriLiteralAnalyzer`, RPAD0255,
// confirmed via re-reading that file's source to be scoped strictly to `ctor.ContainingType ==
// "System.Uri"`), but `UriBuilder` is a genuinely distinct type/constructor not matched by that analyzer.
// Confirmed empirically, matching the agent's report exactly: single exception type,
// `UriFormatException`, for empty text, an embedded space, whitespace-only text, a non-numeric port, and
// multiple colons (ambiguous host:port). `UriBuilder` is notably MORE LENIENT than `Uri` for one thing —
// a scheme-less string like `"example.com"` is silently defaulted to `"http://example.com/"` rather than
// rejected — confirmed empirically before finalizing the test literal set, per the agent's explicit
// warning not to just reuse `Uri`'s test cases.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidUriBuilderLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0421";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "UriBuilder constructor argument is not a valid URI",
        messageFormat: "'new UriBuilder(...)' always throws UriFormatException at runtime — '{0}' is not a valid URI",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The UriBuilder(string) constructor throws UriFormatException when the input string doesn't parse as a URI.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        if (creation.ArgumentList is not { Arguments: [{ Expression: var arg }] }) return;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.UriBuilder") return;

        try
        {
            _ = new UriBuilder(text);
        }
        catch (UriFormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), text));
        }
    }
}
