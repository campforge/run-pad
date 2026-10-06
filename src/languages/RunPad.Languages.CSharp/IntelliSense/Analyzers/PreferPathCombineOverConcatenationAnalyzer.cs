using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто девяносто девятый проход, batch) — own pick, not a Roslynator port.
// `dir + "\\" + file` (or `dir + "/" + file`) hardcodes one platform's path separator into the
// concatenation — a literal "\\" is not the directory separator on Linux/macOS (it becomes a literal
// backslash character IN the file name instead of joining a path), and a literal "/" is not the
// separator .NET's own Windows path APIs normalize on. `Path.Combine(dir, file)` picks the correct
// separator for the running OS and also avoids doubled/missing separators at the join point.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferPathCombineOverConcatenationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0187";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer Path.Combine over concatenating a hardcoded path separator",
        messageFormat: "Concatenating a hardcoded path-separator literal is not portable across operating systems — use 'Path.Combine(...)' so the separator matches the running OS",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A literal \"\\\\\" or \"/\" used to join two path segments only works on the operating system that separator belongs to — Path.Combine picks the correct separator for the OS the code actually runs on.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.AddExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var outer = (BinaryExpressionSyntax)context.Node;
        if (outer.Left is not BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.AddExpression,
                Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } separatorLiteral,
            })
        {
            return;
        }
        if (separatorLiteral.Token.ValueText is not ("\\" or "/"))
            return;

        var ct = context.CancellationToken;
        var type = context.SemanticModel.GetTypeInfo(outer, ct).Type;
        if (type?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, outer.GetLocation()));
    }
}
