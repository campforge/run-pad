using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 147, batch) — idea ported from Roslynator's
// AvoidLockingOnPubliclyAccessibleInstanceAnalyzer (Apache License 2.0,
// https://github.com/dotnet/roslynator), narrowed to its single most unambiguous case: `lock (this)`.
// Any external caller holding a reference to the same object can lock on it too, deadlocking
// unrelated code — the standard fix is a private, dedicated lock object.
// Extended (двести тридцать шестой проход, batch) to also cover `lock (typeof(X))` and `lock
// ("a literal string")` — same underlying "publicly/globally shared lock target" problem as `lock
// (this)`: a `Type` object is one instance shared by every piece of code in the process, and a string
// LITERAL is interned by the CLR (so the exact same string instance can be shared across unrelated
// assemblies) — both let unrelated code lock on the same object and deadlock, same fix (a private,
// dedicated lock object). Deliberately restricted to a literal string, not any string-typed expression
// — a non-literal string isn't guaranteed to be interned, so flagging every `lock(someString)` would be
// over-broad.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AvoidLockOnThisAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0014";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Avoid locking on a publicly/globally accessible object",
        messageFormat: "{0} — use a private, dedicated lock object instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Any external caller with access to the same object (this, a Type, or an interned string literal) can lock on it too, causing an unrelated deadlock.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LockStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var lockStatement = (LockStatementSyntax)context.Node;
        var expression = lockStatement.Expression;

        if (expression.IsKind(SyntaxKind.ThisExpression))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, expression.GetLocation(),
                "Locking on 'this' can deadlock with unrelated code that also holds a reference to this instance"));
            return;
        }

        if (expression is TypeOfExpressionSyntax)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, expression.GetLocation(),
                "Locking on a Type object can deadlock with unrelated code elsewhere in the process that locks on the same Type"));
            return;
        }

        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression })
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, expression.GetLocation(),
                "Locking on a string literal can deadlock with unrelated code that locks on the same interned string"));
        }
    }
}
