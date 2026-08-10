using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят первый проход, batch) — own pick, not a Roslynator port. Sibling of
// InvalidUnicodeCodePointArgumentAnalyzer (RPAD0325, same проход) — same "run the real validator"
// theme, but on the CHAR-based (not int-codepoint-based) half of Unicode surrogate validity: `new
// System.Text.Rune(char ch)` throws ArgumentOutOfRangeException when `ch` is a lone surrogate half
// (0xD800..0xDFFF), and `char.ConvertToUtf32(char highSurrogate, char lowSurrogate)` throws
// ArgumentOutOfRangeException when the pair isn't a valid high/low surrogate pair — confirmed
// empirically via scratch `dotnet run` script. Both pure, side-effect-free managed calls, safe to probe
// live inside a try/catch during analysis.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRuneOrSurrogatePairConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0326";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Argument is not a valid Rune char or surrogate pair",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new Rune(char) throws ArgumentOutOfRangeException for a lone surrogate half, and char.ConvertToUtf32(char, char) throws ArgumentOutOfRangeException when the two chars don't form a valid high/low surrogate pair.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ConvertToUtf32" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var highArg }, { Expression: var lowArg }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(highArg, ct).Value is not char high) return;
        if (context.SemanticModel.GetConstantValue(lowArg, ct).Value is not char low) return;

        bool alwaysThrows;
        try
        {
            _ = char.ConvertToUtf32(high, low);
            alwaysThrows = false;
        }
        catch (ArgumentOutOfRangeException)
        {
            alwaysThrows = true;
        }
        if (!alwaysThrows) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ConvertToUtf32" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_Char) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), "not a valid high/low surrogate pair"));
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not char ch) return;

        bool alwaysThrows;
        try
        {
            _ = new System.Text.Rune(ch);
            alwaysThrows = false;
        }
        catch (ArgumentOutOfRangeException)
        {
            alwaysThrows = true;
        }
        if (!alwaysThrows) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Text.Rune") return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Char }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), creation.ToString(), "a lone surrogate half is not a valid Rune"));
    }
}
