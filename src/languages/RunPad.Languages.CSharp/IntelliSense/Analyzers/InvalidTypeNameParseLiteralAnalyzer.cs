using System.Collections.Immutable;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the eighth research subagent survey, re-verified
// independently before implementing. `System.Reflection.Metadata.TypeName.Parse(ReadOnlySpan<char>,
// TypeNameParseOptions options = default)` (.NET 9+, new to this catalog) — parses reflection type-name
// syntax (`AssemblyQualifiedName`-shaped strings) WITHOUT ever resolving/loading an actual `Type`, so unlike
// `Type.GetType` this has no calling-assembly-context dependency. A string literal binds to the first
// parameter via the implicit `string`→`ReadOnlySpan<char>` conversion; scoped strictly to the 1-argument
// call shape (the `options` parameter omitted) to avoid an explicit non-default `TypeNameParseOptions`
// changing parse leniency underneath this analyzer, same discipline `InvalidBase64UrlLiteralAnalyzer`
// (RPAD0406) already established for avoiding sibling-overload semantic drift. Confirmed empirically, single
// exception type: `ArgumentException`, ONLY for an empty/whitespace-only string or a gross bracket-count
// imbalance (`"System.Int32["`, `"System.Int32]"`, `"...List\`1[["`) or a leading stray `[`/`&` character.
// Confirmed SURPRISINGLY lenient beyond that — do not hand-roll a stricter check, just run the real parser:
// array/pointer/byref suffixes (`[]`, `[,]`, `[*]`, `*`, `&`), a double-dot namespace (`"System..Int32"`), a
// non-numeric/malformed generic-arity backtick marker, an actual mismatch between the backtick arity number
// and the real bracketed generic-argument count, and even embedded spaces/`!` mid-string (`"System.Int32 !
// garbage"`) are all accepted without throwing — none of these are flagged, confirmed matching the agent's
// report exactly. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP/tr-TR, and confirmed no assembly-load
// side effect (a syntactically well-formed but entirely fictional assembly-qualified name parses cleanly).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTypeNameParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0432";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TypeName.Parse argument is not a well-formed reflection type name",
        messageFormat: "'TypeName.Parse(...)' always throws ArgumentException at runtime — '{0}' is not a well-formed reflection type name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TypeName.Parse(ReadOnlySpan<char>) throws ArgumentException for an empty/whitespace-only string, an unbalanced bracket count, or a leading stray '[' or '&' character.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string name) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Parse",
                IsStatic: true,
                Parameters: [{ Type: { } paramType }, ..],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (paramType.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.TypeName") return;

        try
        {
            _ = TypeName.Parse(name);
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), name));
        }
    }
}
