using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят шестой проход, batch) — own pick, not a Roslynator port. A different
// shape than SuspiciousTypeNameStringComparisonAnalyzer (RPAD0087, this project — that one is about
// `GetType().Name == "..."`, a reflection-based type-name check). This is `enumValue.ToString() ==
// "Foo"` — comparing an enum's formatted name to a string literal instead of comparing the enum value
// directly. Slower (formats the whole enum to a string just to throw the string away), and fragile in
// a way direct comparison isn't: a `[Flags]` enum's ToString() can format as a comma-joined list of
// member names for combined values, silently breaking the string match, and a typo in the literal is a
// silent runtime miss instead of a compile error.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDirectEnumComparisonOverToStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0141";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Comparing an enum's ToString() to a string literal",
        messageFormat: "Comparing '{0}.ToString()' to a string literal is slower and more fragile than comparing the enum value directly",
        category: "Reliability",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "enumValue.ToString() == \"Name\" formats the enum to a string only to throw it away — comparing the enum value directly is faster and doesn't silently mismatch for [Flags] combinations or typo'd literals.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        var (enumToStringCall, stringLiteral) = (binary.Left, binary.Right) switch
        {
            (InvocationExpressionSyntax call, LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal) => (call, literal),
            (LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal, InvocationExpressionSyntax call) => (call, literal),
            _ => (null, null),
        };
        if (enumToStringCall is null || stringLiteral is null) return;

        if (enumToStringCall.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" } toStringAccess)
            return;
        if (enumToStringCall.ArgumentList.Arguments.Count != 0) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(enumToStringCall, ct).Symbol is not IMethodSymbol { Name: "ToString" })
            return;

        var receiverType = context.SemanticModel.GetTypeInfo(toStringAccess.Expression, ct).Type;
        if (receiverType is not { TypeKind: TypeKind.Enum } enumType) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), enumType.Name));
    }
}
