using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто восемьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `IntPtr.Size == 8` is the old pointer-size trick for detecting a 64-bit process — `Environment.
// Is64BitProcess` says the same thing directly, by name, without relying on the reader knowing that a
// native pointer happens to be 8 bytes wide on 64-bit platforms.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferIs64BitProcessOverIntPtrSizeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0175";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Environment.Is64BitProcess over IntPtr.Size == 8",
        messageFormat: "'IntPtr.Size == 8' can be simplified to 'Environment.Is64BitProcess' — says the same thing by name instead of via pointer size",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "IntPtr.Size == 8 is the classic pointer-size trick for detecting a 64-bit process — Environment.Is64BitProcess says the same thing directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        var sizeAccess = (binary.Left, binary.Right) switch
        {
            (MemberAccessExpressionSyntax access, LiteralExpressionSyntax { Token.Value: 8 }) => access,
            (LiteralExpressionSyntax { Token.Value: 8 }, MemberAccessExpressionSyntax access) => access,
            _ => null,
        };
        if (sizeAccess is not { Name.Identifier.Text: "Size" }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(sizeAccess, ct).Symbol is not IPropertySymbol { Name: "Size", IsStatic: true } sizeProperty)
            return;
        if (sizeProperty.ContainingType?.SpecialType != SpecialType.System_IntPtr) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }
}
