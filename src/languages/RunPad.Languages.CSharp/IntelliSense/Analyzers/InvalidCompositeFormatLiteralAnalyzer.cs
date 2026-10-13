using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Text.CompositeFormat.Parse(string format)` — a
// static factory (.NET 8+) that pre-parses composite-format-string grammar (`{index[,alignment][:formatString]}`)
// so it doesn't need to be re-parsed on every `.Format(...)` call. Confirmed empirically: this is a pure
// syntax validator, independent of what arguments would later be passed to `.Format(...)` — an unbalanced/
// unterminated brace, a stray `}`, or a non-digit where an index is expected always throws
// `FormatException` (no `ParamName` — `FormatException` is not an `ArgumentException`; only the SEPARATE
// null-literal case throws `ArgumentNullException` with `ParamName` = "format", not reachable via a
// non-null string literal). Properly-escaped `{{`/`}}` is valid and does not throw. No
// `[SupportedOSPlatform]` gating, no side effects — pure string-grammar validation, confirmed independent
// of the ambient `CultureInfo.CurrentCulture` (format-string grammar itself has no culture dimension,
// unlike the VALUES later substituted into it).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCompositeFormatLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0521";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CompositeFormat.Parse argument is not a valid composite format string",
        messageFormat: "'CompositeFormat.Parse(...)' always throws FormatException at runtime — '{0}' is not a valid composite format string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CompositeFormat.Parse(string format) throws FormatException for a format string with unbalanced/unterminated braces, a stray '}', or a non-digit where an index is expected.");

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
        if (invocation.ArgumentList.Arguments is not [{ Expression: var formatArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(formatArg, ct).Value is not string format) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Parse",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Text.CompositeFormat") return;

        try
        {
            _ = CompositeFormat.Parse(format);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), format));
        }
    }
}
