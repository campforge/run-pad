using System.Collections.Immutable;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Reflection.Metadata.AssemblyNameInfo.Parse(ReadOnlySpan<char>)`
// (.NET 9+, new to this catalog — a distinct type from the already-covered `System.Reflection.AssemblyName`
// constructor, RPAD0385) — a string literal binds via the implicit `string`→`ReadOnlySpan<char>` conversion,
// same call shape already established by `Base64Url.DecodeFromChars` (RPAD0406). Confirmed empirically,
// single exception type: `ArgumentException` for an empty/whitespace-only string, a malformed `Version=`
// value (non-numeric, or more than four dot-separated components), a `PublicKeyToken=` that isn't exactly
// 16 hex characters, or a stray unrecognized qualifier field with no `=` (e.g. a bare token after a comma).
// Unlike `AssemblyName`, this type does NOT throw a second exception type for these — no `FileLoadException`
// path was found, everything malformed throws plain `ArgumentException`. Confirmed leniency worth noting for
// correct test literals: an unrecognized-but-well-formed qualifier field (e.g. `Culture=xx-XX-XX-not-a-
// culture`, an actually-invalid culture tag) is accepted silently, matching `AssemblyName`'s own established
// leniency for unvalidated fields. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP/tr-TR. Confirmed no
// side effects — this is a pure syntactic parser that never attempts to resolve or load an assembly (unlike
// `Type.GetType`'s calling-assembly-context trap): a syntactically well-formed but entirely fictional
// assembly name (single simple name, or with a valid `Version=` qualifier) parses successfully with no probe
// for the assembly's actual existence.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidAssemblyNameInfoLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0431";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "AssemblyNameInfo.Parse argument is not a valid assembly display name",
        messageFormat: "'AssemblyNameInfo.Parse(...)' always throws ArgumentException at runtime — '{0}' is not a valid assembly display name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "AssemblyNameInfo.Parse(ReadOnlySpan<char>) throws ArgumentException for an empty/whitespace-only string, a malformed Version qualifier, a malformed PublicKeyToken qualifier, or a stray unrecognized qualifier field with no '=' separator.");

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
                Parameters: [{ Type: { } paramType }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (paramType.ToDisplayString() != "System.ReadOnlySpan<char>") return;
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.AssemblyNameInfo") return;

        try
        {
            _ = AssemblyNameInfo.Parse(name);
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), name));
        }
    }
}
