using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (474-й проход, batch) — candidate #1 from the thirty-seventh research subagent survey,
// re-verified independently before implementing. `System.Security.Cryptography.
// RandomNumberGenerator.GetString(ReadOnlySpan<char> choices, int length)` — first touch of this
// specific overload; distinct from the already-covered `GetBytes(int)`/`GetHexString(int, bool)`
// (RPAD0304) and `GetInt32` analyzers (grepped, confirmed neither doc comment mentions `GetString`).
// A string literal argument binds to the `ReadOnlySpan<char> choices` parameter via the implicit
// `string`→`ReadOnlySpan<char>` conversion, and `GetConstantValue` still extracts the literal directly
// off the argument syntax regardless of the parameter's span type (same established pattern already
// used elsewhere in this catalog for span-typed parameters fed string literals). Confirmed empirically,
// two independent findings: a negative `length` literal (with a non-empty `choices`) throws
// `ArgumentOutOfRangeException` (param `length`) — `length == 0` is valid (empty result); an EMPTY
// `choices` literal throws `ArgumentException` (param `choices`, "Span may not be empty.") regardless
// of `length`'s value (tested negative/zero/positive — the choices-empty check runs before the length
// check). Confirmed `length` larger than `choices.Length` does NOT throw (characters repeat, valid).
// No `[SupportedOSPlatform]` gating, no side effects — cryptographically-random but purely in-memory,
// no I/O. Culture-irrelevant (pure length/emptiness checks, no formatting).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRandomNumberGeneratorGetStringArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0515";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "RandomNumberGenerator.GetString argument is invalid",
        messageFormat: "'RandomNumberGenerator.GetString(...)' always throws {0} at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RandomNumberGenerator.GetString(ReadOnlySpan<char> choices, int length) throws ArgumentException for an empty choices string, or ArgumentOutOfRangeException for a negative length.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetString" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "GetString",
                IsStatic: true,
                ContainingType: { } containingType,
                Parameters: [_, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Security.Cryptography.RandomNumberGenerator") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var choicesArg }, { Expression: var lengthArg }])
            return;

        var choicesConstant = context.SemanticModel.GetConstantValue(choicesArg, ct).Value as string;
        var lengthConstant = context.SemanticModel.GetConstantValue(lengthArg, ct).Value as int?;
        if (choicesConstant is null && lengthConstant is null) return;
        // If choices isn't a literal, we can't safely probe (an unknown choices set could be non-empty
        // and any negative-length throw is already independent of choices anyway, but without a real
        // choices value there's nothing meaningful to pass) — require choices to be resolvable.
        if (choicesConstant is null) return;

        try
        {
            _ = RandomNumberGenerator.GetString(choicesConstant, lengthConstant ?? 1);
        }
        catch (System.ArgumentException ex)
        {
            var (exceptionName, reason) = ex switch
            {
                System.ArgumentOutOfRangeException when lengthConstant is not null => ("ArgumentOutOfRangeException", "length must be non-negative"),
                _ when ex.ParamName == "choices" => ("ArgumentException", "choices must not be empty"),
                _ => (null, (string?)null),
            };
            if (exceptionName is null) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), exceptionName, reason));
        }
    }
}
