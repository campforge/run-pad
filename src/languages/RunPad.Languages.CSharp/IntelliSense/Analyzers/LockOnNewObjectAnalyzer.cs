using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A different,
// more severe cousin of AvoidLockOnThisAnalyzer's (RPAD0014) family: that one flags lock targets other
// code could ALSO lock on (a deadlock risk); this one flags `lock (new object())` — a lock target
// NOTHING else can ever lock on, because a brand-new object is allocated every time the statement runs.
// Two threads racing through this lock statement each get their OWN, distinct lock object, so neither
// ever blocks the other — the lock provides no synchronization at all, silently. Scoped to a bare `new
// T()`/`new T { }` object-creation expression directly in the lock's parenthesized expression — a
// variable holding a `new`-created object from elsewhere isn't caught (correctly: only re-allocating on
// every entry is the bug, not merely having been `new`-ed once, further away).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LockOnNewObjectAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0227";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Lock target is allocated fresh on every entry",
        messageFormat: "'lock (new ...())' creates a new lock object every time this statement runs — no two threads ever contend for the same lock, so this provides no synchronization at all",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A lock target created with 'new' directly in the lock statement is a fresh, distinct object on every entry, so the lock never actually excludes concurrent access.");

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
        if (lockStatement.Expression is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            context.ReportDiagnostic(Diagnostic.Create(Rule, lockStatement.Expression.GetLocation()));
    }
}
