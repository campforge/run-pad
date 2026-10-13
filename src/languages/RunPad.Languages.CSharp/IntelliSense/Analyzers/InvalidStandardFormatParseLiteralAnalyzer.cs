using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.StandardFormat.Parse(string format)` —
// a static factory, structurally distinct from the already-covered constructor
// (`InvalidStandardFormatConstructorAnalyzer`, RPAD0492, which only checks `new StandardFormat(char,
// byte)`'s `precision` argument). Confirmed empirically: a format symbol followed by more than 2 digits
// (precision &gt; 99) throws `FormatException` ("Precision cannot be larger than 99."), and a symbol
// followed by any non-digit character throws the same exception type with a different message
// ("Characters following the format symbol must be a number of 99 or less."). No `[SupportedOSPlatform]`
// gating, no receiver to construct (fully static), no side effects — pure string-grammar validation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStandardFormatParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0524";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "StandardFormat.Parse argument is not a valid standard format string",
        messageFormat: "'StandardFormat.Parse(...)' always throws FormatException at runtime — '{0}' is not a valid standard format string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StandardFormat.Parse(string) throws FormatException when the format symbol is followed by more than two digits, or by any non-digit character.");

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
        if (containingType.ToDisplayString() != "System.Buffers.StandardFormat") return;

        try
        {
            _ = StandardFormat.Parse(format);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), format));
        }
    }
}
