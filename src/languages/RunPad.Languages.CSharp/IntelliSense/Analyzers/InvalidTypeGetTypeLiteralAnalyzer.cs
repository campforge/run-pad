using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #4, `Type.GetType(string typeName, bool throwOnError)` — confirmed empirically, matching the
// agent's report on the exception types (`TypeLoadException` for an empty string; `ArgumentException` for
// malformed generic-arity syntax like `"System.String[[["`), but ONLY when `throwOnError` is `true` —
// confirmed `throwOnError: false` never throws (returns `null` instead), so this analyzer only fires when
// the second argument is the literal constant `true`, checked via `GetConstantValue` same as the first.
//
// TWO SAFETY-CRITICAL SCOPE RESTRICTIONS, the second one discovered independently (not in the agent's
// report) while re-verifying:
//
// 1. An assembly-qualified type name (e.g. `"System.String, mscorlib"`) makes the CLR actually attempt to
//    load the named assembly from disk/GAC — a genuine SIDE EFFECT, unlike every other analyzer in this
//    catalog, which is pure in-memory validation. This analyzer refuses to even attempt the real-validator
//    call if the literal contains a comma ANYWHERE — comma is required syntax for assembly qualification
//    and also appears inside bracketed generic-argument lists that could themselves carry assembly-
//    qualified type arguments, so bailing on any comma at all (rather than parsing out "is this comma
//    inside a generic bracket or not") is the safe, conservative choice.
//
// 2. The agent's report treated "well-formed name, but not found" as a flaggable `TypeLoadException`
//    case. Empirically DISPROVEN as safe: unqualified-name resolution for `Type.GetType` depends on the
//    CALLING assembly (confirmed via a scratch test — a type name that resolves fine when the call site is
//    in the SAME assembly as the type's declaration fails to resolve when called from a DIFFERENT
//    assembly). Since this analyzer calls the real API from ITS OWN assembly (`RunPad.Languages.CSharp.
//    dll`), not the user's analyzed project's assembly, a type name that's perfectly valid in the user's
//    own code would be misreported as "not found" — a genuine FALSE POSITIVE risk unique to this
//    candidate among everything else in this catalog. This analyzer therefore does NOT treat "well-formed
//    name, not found" as flaggable at all — it only reports the empty-string case (verified invariant:
//    the null/empty check happens before any assembly-context resolution is even attempted, confirmed via
//    the exact exception message "A null or zero length string does not represent a valid Type.") and the
//    malformed-generic-syntax `ArgumentException` case (also invariant: a syntax error in the grammar
//    itself, thrown before assembly resolution begins).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTypeGetTypeLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0418";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Type.GetType argument is empty or has malformed generic-arity syntax",
        messageFormat: "'Type.GetType(...)' always throws {0} at runtime — '{1}' is empty or has malformed generic-arity syntax",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Type.GetType(string, true) throws TypeLoadException for an empty type name, or ArgumentException for malformed generic-arity syntax — both invariant regardless of calling assembly. A well-formed but unresolved name is deliberately NOT flagged, since resolution depends on the calling assembly and this analyzer runs in a different assembly than the analyzed code.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetType" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var nameArg }, { Expression: var throwArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string typeName) return;
        if (typeName.Contains(',')) return;
        if (context.SemanticModel.GetConstantValue(throwArg, ct).Value is not true) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "GetType",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_Boolean }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Type") return;

        try
        {
            _ = System.Type.GetType(typeName, throwOnError: true);
        }
        catch (ArgumentException ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, typeName));
        }
        catch (TypeLoadException ex) when (typeName.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, typeName));
        }
    }
}
