using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `Console.Out.WriteLine(...)`/`Console.Out.Write(...)` goes through the `Out` TextWriter property
// explicitly — `Console.WriteLine(...)`/`Console.Write(...)` already forward to exactly that writer
// internally, so the `.Out` step says nothing the shorter call doesn't already say.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferConsoleWriteLineOverConsoleOutWriteLineAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0157";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Console.Out before Write/WriteLine",
        messageFormat: "'Console.Out.{0}(...)' can be simplified to 'Console.{0}(...)' — Console.{0} already writes through Out",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Console.WriteLine/Write already forward to Console.Out internally, so going through .Out explicitly says nothing extra.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "WriteLine" or "Write" } methodAccess)
            return;
        if (methodAccess.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Out" } outAccess) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(outAccess, ct).Symbol is not IPropertySymbol { Name: "Out" } outProperty)
            return;
        if (outProperty.ContainingType?.ToDisplayString() != "System.Console") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodAccess.Name.Identifier.Text));
    }
}
