using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто пятидесятый проход, batch) — idea ported from Roslynator's
// EnumShouldDeclareFlagAttribute-adjacent analyzers (Apache License 2.0,
// https://github.com/dotnet/roslynator). Combining enum members with `|`/`&`/`^` only makes sense if
// the enum's values are distinct bit flags — an enum without `[Flags]` almost always uses its default
// 0/1/2/3/... member values, where bitwise-combining two members produces a value that isn't even one
// of the enum's own named members (e.g. Red | Blue on a non-Flags color enum).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BitwiseOperatorOnNonFlagsEnumAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0036";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Bitwise operator used on a non-[Flags] enum",
        messageFormat: "'{0}' is not marked [Flags] — combining its members with a bitwise operator produces a value that isn't one of its named members",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Bitwise-combining enum members only makes sense when the enum's values are distinct bit flags, which requires [Flags] (and flag-shaped member values) to be meaningful.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze, SyntaxKind.BitwiseOrExpression, SyntaxKind.BitwiseAndExpression, SyntaxKind.ExclusiveOrExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        var leftType = context.SemanticModel.GetTypeInfo(binary.Left, ct).Type;
        if (leftType is not { TypeKind: TypeKind.Enum }) return;

        var rightType = context.SemanticModel.GetTypeInfo(binary.Right, ct).Type;
        if (!SymbolEqualityComparer.Default.Equals(leftType, rightType)) return;

        var hasFlags = leftType.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");
        if (hasFlags) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), leftType.Name));
    }
}
