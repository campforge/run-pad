using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported (not copied — own implementation, see
// EmptyCatchClauseAnalyzer's own doc comment for the same rationale) from Roslynator's
// InvalidArgumentNullCheckAnalyzer (RCS1256, Apache License 2.0,
// https://github.com/dotnet/roslynator). A guard clause like `if (a == null) throw new
// ArgumentNullException(nameof(b));` — checking one parameter but naming a DIFFERENT one in the
// exception — is almost always a copy-paste mistake (guard clauses are usually written by copying the
// previous parameter's check and only half-updating it), and the resulting exception message actively
// misleads whoever hits it. Narrow, single well-defined shape — near-zero false-positive risk.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidArgumentNullCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0004";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ArgumentNullException references the wrong parameter",
        messageFormat: "'{0}' is checked for null, but the exception names '{1}' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A null check whose ArgumentNullException names a different identifier than the " +
            "one actually checked is almost always a copy-paste mistake.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var ifStatement = (IfStatementSyntax)context.Node;

        var checkedName = GetNullCheckedIdentifier(ifStatement.Condition);
        if (checkedName is null) return;

        if (UnwrapSingleThrow(ifStatement.Statement)?.Expression is not ObjectCreationExpressionSyntax objectCreation)
            return;

        var type = context.SemanticModel.GetTypeInfo(objectCreation, context.CancellationToken).Type;
        if (type is null || type.ToDisplayString() != "System.ArgumentNullException") return;

        var namedParam = GetFirstArgumentParamName(objectCreation);
        if (namedParam is null || string.Equals(namedParam, checkedName, StringComparison.Ordinal)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, objectCreation.GetLocation(), checkedName, namedParam));
    }

    // Matches `x == null` / `null == x` / `x is null` — the three common spellings of a guard clause's
    // null check. Anything more elaborate (`x is not { }`, a helper method, ...) is deliberately left
    // alone rather than guessed at.
    private static string? GetNullCheckedIdentifier(ExpressionSyntax condition) => condition switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } binary =>
            (binary.Left, binary.Right) switch
            {
                (IdentifierNameSyntax id, var other) when other.IsKind(SyntaxKind.NullLiteralExpression) => id.Identifier.Text,
                (var other, IdentifierNameSyntax id) when other.IsKind(SyntaxKind.NullLiteralExpression) => id.Identifier.Text,
                _ => null,
            },
        IsPatternExpressionSyntax
        {
            Expression: IdentifierNameSyntax id,
            Pattern: ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression },
        } => id.Identifier.Text,
        _ => null,
    };

    private static ThrowStatementSyntax? UnwrapSingleThrow(StatementSyntax statement) => statement switch
    {
        ThrowStatementSyntax t => t,
        BlockSyntax { Statements: [ThrowStatementSyntax t] } => t,
        _ => null,
    };

    // Accepts either the idiomatic `nameof(x)` or a plain string literal `"x"` — both are common in
    // real code, and the mismatch this analyzer looks for is exactly as much of a bug either way.
    private static string? GetFirstArgumentParamName(ObjectCreationExpressionSyntax objectCreation)
    {
        var firstArg = objectCreation.ArgumentList?.Arguments.FirstOrDefault();
        return firstArg?.Expression switch
        {
            InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "nameof" },
                ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax nameofId }],
            } => nameofId.Identifier.Text,
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal => literal.Token.ValueText,
            _ => null,
        };
    }
}
