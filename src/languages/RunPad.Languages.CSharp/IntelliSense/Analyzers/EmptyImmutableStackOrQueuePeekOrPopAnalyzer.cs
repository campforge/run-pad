using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят шестой проход, batch) — own pick, not a Roslynator port. Sibling of
// EmptyQueueOrStackDequeueOrPopAnalyzer (RPAD0291, проходы 319-320), applied to the immutable
// counterparts `ImmutableStack<T>`/`ImmutableQueue<T>` instead of `Stack<T>`/`Queue<T>`:
// `ImmutableStack<T>.Empty.Peek()`/`.Pop()` and `ImmutableQueue<T>.Empty.Peek()`/`.Dequeue()` all throw
// InvalidOperationException — confirmed empirically via scratch `dotnet run` script (same exception
// type and message, "This operation does not apply to an empty instance."). Kept as a SEPARATE analyzer
// from RPAD0291 rather than an extension, because the known-empty RECEIVER shape genuinely differs:
// RPAD0291 detects a freshly constructed `new Queue<T>()`/`new Stack<T>()` (these types have public
// constructors), whereas `ImmutableStack<T>`/`ImmutableQueue<T>` have no public constructor at all —
// their known-empty receiver is the static `.Empty` field instead, same receiver-detection shape as
// InvalidImmutableArrayLiteralIndexerAnalyzer/InvalidImmutableListLiteralIndexerAnalyzer (RPAD0357/0358,
// проход 365). `.Pop()` on `ImmutableStack<T>` returns the popped STACK (not the popped element — that's
// the separate `Pop(out T)` overload), but this doesn't affect detection since only the throw/no-throw
// outcome is being proven, not the return value's shape.
// Self-correction (проход 366): the FIRST version only matched `Empty` resolving to an `IFieldSymbol`
// (copying RPAD0357/0358's own `.Empty` check verbatim) — a single-item PROBE for
// `ImmutableStack<int>.Empty.Peek()` gave a TIMEOUT (no diagnostic at all). A reflection-based scratch
// check (`typeof(ImmutableStack<>).GetMember("Empty")`) revealed a genuine, easy-to-miss API
// inconsistency across the six Immutable collection types touched that проход: `ImmutableArray`/
// `ImmutableList`/`ImmutableHashSet`/`ImmutableSortedSet`/`ImmutableDictionary`/
// `ImmutableSortedDictionary`'s `Empty` are all plain static FIELDS, but `ImmutableStack`/
// `ImmutableQueue`'s `Empty` are static PROPERTIES instead — confirmed via reflection, not assumed.
// Fixed by accepting either `IFieldSymbol` or `IPropertySymbol` named `Empty`.
// Extended in the триста шестьдесят седьмой проход to also cover the 1-arg `Pop(out T)`/`Dequeue(out T)`
// overloads (own doc comment above already flagged these as a known, deliberate gap) — confirmed
// empirically to throw identically to their 0-arg counterparts. `Peek` has no such overload (confirmed:
// only one `Peek()` signature exists), so the argument-count ceiling is simply relaxed to 1 and left to
// the existing end-of-method symbol-resolution check to reject `Peek(out x)` naturally (no such overload
// exists to resolve to, so `GetSymbolInfo` fails to match and the analyzer bails out, same as any other
// unresolvable call). Message format's fixed `{1}()` suffix is now a single pre-formatted `{1}` value
// (`"Peek()"` / `"Pop(out ...)"` / etc.), chosen so the EXISTING 0-arg case's rendered text is byte-for-
// byte unchanged (`"Peek()"` either way) — no existing test needed updating.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EmptyImmutableStackOrQueuePeekOrPopAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0360";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Peek/Pop/Dequeue on ImmutableStack<T>.Empty/ImmutableQueue<T>.Empty always throws",
        messageFormat: "'{0}.Empty.{1}' always throws InvalidOperationException at runtime — the receiver is the known-empty singleton",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ImmutableStack<T>.Peek()/Pop() and ImmutableQueue<T>.Peek()/Dequeue() throw InvalidOperationException when called on an empty instance — provably true when the receiver is the ImmutableStack<T>.Empty/ImmutableQueue<T>.Empty static field directly.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Peek" or "Pop" or "Dequeue" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        var argCount = invocation.ArgumentList.Arguments.Count;
        if (argCount > 1) return;

        if (access.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Empty" } emptyAccess)
            return;

        var ct = context.CancellationToken;
        var emptyType = context.SemanticModel.GetSymbolInfo(emptyAccess, ct).Symbol switch
        {
            IFieldSymbol { Name: "Empty" } field => field.ContainingType,
            IPropertySymbol { Name: "Empty" } property => property.ContainingType,
            _ => null,
        };
        if (emptyType is not { } fieldType) return;
        var simpleTypeName = fieldType.OriginalDefinition.Name;
        if ((simpleTypeName != "ImmutableStack" || methodName is not ("Peek" or "Pop"))
            && (simpleTypeName != "ImmutableQueue" || methodName is not ("Peek" or "Dequeue")))
        {
            return;
        }
        if (fieldType.OriginalDefinition.ContainingNamespace?.ToDisplayString() != "System.Collections.Immutable")
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName, ContainingType: { } containingType, Parameters.Length: var paramCount }
            || resolvedName != methodName
            || containingType.OriginalDefinition.Name != simpleTypeName
            || paramCount != argCount)
        {
            return;
        }

        var callSuffix = methodName + (argCount == 1 ? "(out ...)" : "()");
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), simpleTypeName, callSuffix));
    }
}
