using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "generic overload avoids the Type/object round trip" theme as PreferGenericEnumStaticMethodAnalyzer
// (RPAD0090, this project, for Enum.Parse/IsDefined/GetName). `Marshal.SizeOf(typeof(T))` boxes through
// `Type` just to look up a size the generic `Marshal.SizeOf<T>()` (available since this project's SDK
// floor) already returns directly. Verified empirically (a real throwaway build, not assumed) that the
// generic overload is a drop-in replacement with no extra constraints tripping up ordinary struct/class
// arguments.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferGenericMarshalSizeOfAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0176";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use the generic Marshal.SizeOf<T>() overload",
        messageFormat: "Use 'Marshal.SizeOf<T>()' instead of 'Marshal.SizeOf(typeof(T))' — avoids the Type round trip",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Marshal.SizeOf(typeof(T)) looks up a Type object just to read a size; the generic overload does the same lookup directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SizeOf" }) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: TypeOfExpressionSyntax }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "SizeOf", IsGenericMethod: false } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Runtime.InteropServices.Marshal") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
