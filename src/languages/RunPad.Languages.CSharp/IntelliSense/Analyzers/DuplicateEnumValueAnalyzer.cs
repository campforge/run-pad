using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 142) — idea ported (not copied — own implementation, see
// EmptyCatchClauseAnalyzer's own doc comment for the same rationale) from Roslynator's
// DuplicateEnumValueAnalyzer (RCS1234, Apache License 2.0, https://github.com/dotnet/roslynator).
// Two enum members sharing the same underlying value is almost always a copy-paste mistake — nearly
// zero false-positive risk (unlike e.g. an "unused parameter" analyzer, deliberately NOT picked for
// this first batch — see TODO.md #74's own note on why).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateEnumValueAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Duplicate enum value",
        messageFormat: "Enum member '{0}' has the same underlying value as '{1}'",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Two enum members with the same underlying value are almost always a " +
            "copy-paste mistake rather than an intentional alias.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeEnumDeclaration, SyntaxKind.EnumDeclaration);
    }

    private static void AnalyzeEnumDeclaration(SyntaxNodeAnalysisContext context)
    {
        var enumDeclaration = (EnumDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;
        var ct = context.CancellationToken;

        var seen = new Dictionary<object, string>();

        foreach (var member in enumDeclaration.Members)
        {
            // A member combining OTHER named members via a bitwise operator (e.g. `Both = Red | Blue`,
            // the standard [Flags] "named combination" idiom) isn't a copy-paste accident even when its
            // resulting value collides with an existing member — deliberately not flagged, to keep this
            // analyzer's false-positive rate near zero for [Flags] enums. Its value is still recorded
            // in `seen` below, so a LATER genuine literal duplicate of that same value is still caught.
            var isNamedCombination = member.EqualsValue?.Value is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.BitwiseOrExpression or (int)SyntaxKind.BitwiseAndExpression
                    or (int)SyntaxKind.ExclusiveOrExpression,
            };

            if (semanticModel.GetDeclaredSymbol(member, ct) is not IFieldSymbol { HasConstantValue: true } symbol)
                continue;

            var value = symbol.ConstantValue!;
            if (seen.TryGetValue(value, out var firstName))
            {
                if (!isNamedCombination)
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule, member.Identifier.GetLocation(), member.Identifier.Text, firstName));
            }
            else
            {
                seen[value] = member.Identifier.Text;
            }
        }
    }
}
