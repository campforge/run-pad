using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; last remaining
// high-confidence candidate from the research subagent's ranked list (проход 377), confirmed empirically
// before implementing. Same "run the real validator via try/catch around the real constructor" theme as
// InvalidCultureNameLiteralAnalyzer/InvalidRegionInfoLiteralAnalyzer (RPAD0248/RPAD0372), applied to the
// single-string `System.Runtime.Versioning.FrameworkName(string)` constructor (deliberately NOT the
// `(string, Version)`/`(string, Version, string)` overloads — those take an already-constructed `Version`
// object, a different, non-literal-string concern). Confirmed empirically this is a single-exception-type
// situation (unlike RPAD0373's ContentType): EVERY failure mode — empty string, wrong component count
// (must be exactly "Identifier,Version=vX.Y" or "Identifier,Version=vX.Y,Profile=Z"), or an unparseable
// Version component — throws plain `ArgumentException`, never a more specific subtype.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidFrameworkNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0374";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "FrameworkName literal is not a valid framework name string",
        messageFormat: "'new FrameworkName(...)' always throws ArgumentException at runtime — '{0}' is not a valid FrameworkName string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The FrameworkName(string) constructor throws ArgumentException for a string that isn't exactly \"Identifier,Version=vX.Y\" or \"Identifier,Version=vX.Y,Profile=Z\".");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var nameArg }] }) return;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string name) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
            } method)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Runtime.Versioning.FrameworkName") return;

        try
        {
            _ = new FrameworkName(name);
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), name));
        }
    }
}
