using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят пятый проход, batch) — own pick, not a Roslynator port.
// `n.ToString("G")` spells out the format specifier that the parameterless `n.ToString()` already uses
// by default for numeric types — genuinely redundant, not just a style nit. Deliberately scoped to the
// built-in numeric types only, where "G" being the implicit default is documented .NET behavior — NOT
// extended to types like Guid ("D" is the default, "G" isn't even a valid specifier for Guid) or
// TimeSpan (whose default is "c", not "G"), where the same-looking assumption would be flatly wrong.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantGeneralFormatStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0094";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant \"G\" format string",
        messageFormat: "'ToString(\"G\")' is already the default format for this type — use 'ToString()' directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "\"G\" (general) is the default format .NET's numeric types use when ToString() is called with no format string, so specifying it explicitly is redundant.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<SpecialType> NumericTypesWithGDefault = ImmutableHashSet.Create(
        SpecialType.System_Byte, SpecialType.System_SByte, SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" } memberAccess)
            return;
        if (invocation.ArgumentList.Arguments is not
            [{ Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal }])
        {
            return;
        }
        if (literal.Token.ValueText != "G") return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToString" }) return;

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;
        if (receiverType is null || !NumericTypesWithGDefault.Contains(receiverType.SpecialType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
