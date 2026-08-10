using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят первый проход, batch) — own pick, not a Roslynator port. Same "run the
// real validator" theme as InvalidGuidLiteralFormatAnalyzer/InvalidDateTimeConstructorArgumentAnalyzer
// (RPAD0246/RPAD0264): `char.ConvertFromUtf32(int utf32)` and `new System.Text.Rune(int value)` both
// throw ArgumentOutOfRangeException for a literal that isn't a valid Unicode code point — not in
// 0x000000..0x10FFFF, or falling inside the surrogate range 0xD800..0xDFFF — confirmed empirically via
// scratch `dotnet run` script (both methods use the identical valid-range rule, just spelled as a
// static method vs. a struct constructor) rather than hand-deriving the boundary arithmetic. Both are
// pure, side-effect-free managed calls (unlike RPAD0322's `Semaphore`, which wraps a live OS handle),
// so probing them live inside a try/catch during analysis is safe. A sibling analyzer,
// InvalidRuneOrSurrogatePairConstructorArgumentAnalyzer (RPAD0326, same проход), covers the related but
// mechanically different CHAR-based surrogate validity checks (`new Rune(char)`/`char.ConvertToUtf32
// (char, char)`) rather than this int-codepoint-based one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidUnicodeCodePointArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0325";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Argument is not a valid Unicode code point",
        messageFormat: "'{0}({1})' always throws ArgumentOutOfRangeException at runtime — {1} is not a valid Unicode code point",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "char.ConvertFromUtf32(int) and new Rune(int) throw ArgumentOutOfRangeException when the argument isn't a valid Unicode code point (outside 0x000000..0x10FFFF, or inside the surrogate range 0xD800..0xDFFF).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ConvertFromUtf32" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not int codePoint) return;
        if (!AlwaysThrows(codePoint)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ConvertFromUtf32" } method)
            return;
        if (method.ContainingType?.SpecialType != SpecialType.System_Char) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), "char.ConvertFromUtf32", codePoint));
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var arg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not int codePoint) return;
        if (!AlwaysThrows(codePoint)) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Text.Rune") return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "new Rune", codePoint));
    }

    private static bool AlwaysThrows(int codePoint)
    {
        try
        {
            _ = char.ConvertFromUtf32(codePoint);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
