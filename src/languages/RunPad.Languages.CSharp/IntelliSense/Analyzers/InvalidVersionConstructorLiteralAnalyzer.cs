using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (429-й проход, batch) — own pick, not a Roslynator port; direct brainstorming resumed after
// both research subagents' candidate lists (проходы 411-421, 424-428) were exhausted. Found by checking
// whether `InvalidVersionLiteralFormatAnalyzer` (RPAD0247, проход 254) — which registers ONLY on
// `SyntaxKind.InvocationExpression` and matches `Name.Identifier.Text: "Parse"` — also covers the
// `Version(string)` constructor. Confirmed via direct source reading it does not, and its own doc comment
// never mentions the constructor as a deliberately-excluded case, unlike RPAD0246's explicit scoping notes
// — this looks like a genuine oversight rather than a documented decision, the same shape of gap as
// RPAD0402 (`new Guid(string)` vs `Guid.Parse`, проход 411). Rather than extend RPAD0247's hand-rolled
// regex to also handle `ObjectCreationExpression` (which would require duplicating its component-count
// logic), this is a NEW separate analyzer using the "run the real validator" technique instead — matching
// RPAD0402's own choice to prefer the real API over regex duplication. Confirmed empirically: THREE
// exception types depending on failure shape — `ArgumentException` for too-few/too-many dot-separated
// components, `FormatException` for a non-numeric component, and `ArgumentOutOfRangeException` (a subtype
// of `ArgumentException`, so a single `catch (ArgumentException or FormatException)` already covers all
// three) for a negative component.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidVersionConstructorLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0419";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Version constructor argument does not match the major.minor[.build[.revision]] format",
        messageFormat: "'new Version(...)' always throws {0} at runtime — this literal doesn't have 2 to 4 dot-separated non-negative integer components (major.minor[.build[.revision]])",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The Version(string) constructor requires 2 to 4 dot-separated non-negative integer components; anything else throws ArgumentException, FormatException, or ArgumentOutOfRangeException.");

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
        if (containingType.ToDisplayString() != "System.Version") return;

        try
        {
            _ = new Version(text);
        }
        catch (System.Exception ex) when (ex is ArgumentException or FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), ex.GetType().Name));
        }
    }
}
