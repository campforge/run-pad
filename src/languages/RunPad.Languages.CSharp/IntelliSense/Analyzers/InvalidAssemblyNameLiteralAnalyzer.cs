using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379-RPAD0384. Same "run the real validator" theme, applied to
// `new System.Reflection.AssemblyName(string)` — one of only two constructors (parameterless and this one,
// confirmed via reflection). Confirmed empirically this is genuinely a TWO-exception-type situation, same
// class as InvalidContentTypeLiteralAnalyzer (RPAD0373): an EMPTY string throws `ArgumentException`
// specifically, while a non-empty string with a malformed comma-separated qualifier section (an
// unparseable `Version=`, an invalid-hex `PublicKeyToken=`/`PublicKey=`, a stray/empty field, or a missing
// comma before the qualifier section) throws `System.IO.FileLoadException` instead — a genuinely
// surprising exception type for what looks like a plain string-format problem. IMPORTANT LENIENCY CONFIRMED
// EMPIRICALLY before picking test literals: the "simple name" portion is barely validated at all (even
// `"///invalid???"` with no qualifier section is accepted as a literal simple name), and any UNRECOGNIZED
// field name in the qualifier section (e.g. `Culture=badculture`, `Culture=` with a value AssemblyName
// doesn't validate) is silently ignored rather than rejected — so this analyzer only catches the narrower
// set of qualifier fields (`Version`/`PublicKeyToken`/`PublicKey`) and syntax shapes AssemblyName actually
// parses strictly, confirmed empirically rather than assumed from the field's apparent purpose.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidAssemblyNameLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0385";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "AssemblyName literal is not a valid assembly display name",
        messageFormat: "'new AssemblyName(...)' always throws {0} at runtime — '{1}' is not a valid assembly display name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The AssemblyName(string) constructor throws ArgumentException for an empty string, or FileLoadException for a string with a malformed Version/PublicKeyToken/PublicKey qualifier.");

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
        if (method.ContainingType?.ToDisplayString() != "System.Reflection.AssemblyName") return;

        try
        {
            _ = new AssemblyName(name);
        }
        // `Culture=` validation is platform-dependent (Windows and invariant-globalization mode reject
        // `Culture=badculture` with CultureNotFoundException, an ArgumentException subclass; ICU on
        // macOS/Linux accepts it), so a culture failure is not an "always throws" case and is deliberately
        // swallowed without a diagnostic rather than escaping the analyzer.
        catch (System.Globalization.CultureNotFoundException)
        {
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.IO.FileLoadException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), ex.GetType().Name, name));
        }
    }
}
