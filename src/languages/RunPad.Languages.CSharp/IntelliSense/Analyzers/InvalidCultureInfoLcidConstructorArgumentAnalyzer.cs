using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231), applied to `new
// CultureInfo(int culture)` (the LCID-based constructor, distinct from `InvalidCultureNameLiteralAnalyzer`
// /RPAD0247, which validates the STRING-name constructor): throws ArgumentOutOfRangeException for a
// negative `culture` value — confirmed empirically via scratch `dotnet run` script. Deliberately does
// NOT attempt to flag a positive-but-unassigned LCID (e.g. 999999) as always-invalid — unlike a
// negative literal, whether a given positive LCID is a real, registered culture is an ICU/platform data
// question, not a pure syntactic one, so this stays scoped to the one condition that's unconditionally
// invalid everywhere: negative.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCultureInfoLcidConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0343";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CultureInfo LCID constructor argument is negative",
        messageFormat: "'new CultureInfo(...)' always throws ArgumentOutOfRangeException at runtime — culture cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new CultureInfo(culture) throws ArgumentOutOfRangeException when the LCID argument is negative.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var cultureArg }, ..] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(cultureArg, ct).Value is not int culture || culture >= 0)
            return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Globalization.CultureInfo") return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }, ..]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }
}
