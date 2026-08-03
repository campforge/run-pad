using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 151, batch) — a well-known, widely-documented async pitfall
// (not a Roslynator port — same "real bug, own pick" bar as SelfReferencingPropertyAnalyzer/
// RethrowToPreserveStackTraceAnalyzer/CompareNaNCorrectlyAnalyzer): an exception thrown inside an
// `async void` method can't be awaited or caught by its caller — it escapes straight to
// SynchronizationContext.Current (or crashes the process if there is none), instead of completing a
// Task the caller could observe. Deliberately excludes the one legitimate use — event handlers, which
// the delegate signature (object sender, EventArgs e) forces to be void — everything else should be
// `async Task`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AvoidAsyncVoidAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0040";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Avoid async void",
        messageFormat: "An exception thrown in an 'async void' method can't be awaited or caught by its caller — use 'async Task' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "async void methods can't be awaited, so an exception inside one escapes to the SynchronizationContext (or crashes the process) instead of completing a Task the caller could observe.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword)) return;
        if (method.ReturnType is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword }) return;
        if (IsEventHandlerShaped(context, method)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation()));
    }

    private static bool IsEventHandlerShaped(SyntaxNodeAnalysisContext context, MethodDeclarationSyntax method)
    {
        if (method.ParameterList.Parameters is not [{ Type: { } firstType }, { Type: { } secondType }]) return false;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetTypeInfo(firstType, ct).Type is not { SpecialType: SpecialType.System_Object })
            return false;

        for (var t = context.SemanticModel.GetTypeInfo(secondType, ct).Type; t is not null; t = t.BaseType)
        {
            if (t.ToDisplayString() == "System.EventArgs") return true;
        }

        return false;
    }
}
