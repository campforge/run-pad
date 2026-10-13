using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `Path.GetRelativePath(string relativeTo, string path)` was
// deliberately EXCLUDED from InvalidPathEmbeddedNulLiteralAnalyzer's (RPAD0423) scope, whose own doc
// comment names it explicitly as "left for a possible follow-up rather than rushed here" — this is that
// follow-up, plus one additional failure mode the agent found that RPAD0423 doesn't check for at all: an
// exact empty-string literal for EITHER argument throws `ArgumentException` ("The path is empty."),
// confirmed independent of the other argument's runtime value (each internal check runs in sequence and
// throws before the other argument is ever inspected, confirmed by testing both directions). Confirmed
// this is a DIFFERENT trigger from whitespace-only strings, which the real API accepts without throwing —
// only the literal empty string `""` (never `" "`) is flagged for that reason. The embedded-NUL check
// reuses RPAD0423's own confirmed-invariant reasoning (NUL is the only universally-invalid path character
// across platforms) but is implemented as its own analyzer rather than folded into RPAD0423, since this
// method's two-string-argument shape (checking EITHER of two positional arguments independently) doesn't
// fit that analyzer's existing single-argument dispatch switch.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPathGetRelativePathLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0430";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Path.GetRelativePath argument is empty or contains an embedded NUL character",
        messageFormat: "'Path.GetRelativePath(...)' always throws ArgumentException at runtime — the '{0}' argument {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Path.GetRelativePath(relativeTo, path) throws ArgumentException when either argument is the exact empty string, or contains an embedded NUL ('\\0') character.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetRelativePath" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var relativeToArg }, { Expression: var pathArg }])
            return;

        var ct = context.CancellationToken;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary,
                IsStatic: true,
                Name: "GetRelativePath",
                Parameters: [{ Type.SpecialType: SpecialType.System_String }, { Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.IO.Path") return;

        if (TryDescribeInvalid(context.SemanticModel, relativeToArg, ct, out var relativeToReason))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "relativeTo", relativeToReason));
            return;
        }
        if (TryDescribeInvalid(context.SemanticModel, pathArg, ct, out var pathReason))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "path", pathReason));
        }
    }

    private static bool TryDescribeInvalid(SemanticModel semanticModel, ExpressionSyntax argExpression, System.Threading.CancellationToken ct, out string reason)
    {
        reason = "";
        if (semanticModel.GetConstantValue(argExpression, ct).Value is not string value) return false;

        if (value.Length == 0)
        {
            reason = "is empty";
            return true;
        }
        if (value.Contains('\0'))
        {
            reason = "contains an embedded NUL ('\\0') character";
            return true;
        }
        return false;
    }
}
