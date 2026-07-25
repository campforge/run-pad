using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotPad.Languages.CSharp;

/// <summary>
/// Inspects a <see cref="DotPad.Format.RPad.RpadKind.Program"/> script's <c>Main</c> local function
/// signature — <see cref="RpadKindTransform"/> needs both facts captured in <see cref="MainSignature"/>
/// to build the right call statement: <c>IsStatic</c> picks between <c>__DotPadProgram.Main()</c> and
/// <c>new __DotPadProgram().Main()</c> (a static local function can't be called through an instance
/// reference — CS0176), and <c>ReturnsTask</c> (<c>async Task Main()</c>/<c>Task Main()</c>/generic
/// <c>Task&lt;T&gt;</c> forms) picks between a fire-and-forget call and an awaited one, so an async
/// Main's work (and any exception thrown inside it) actually gets awaited instead of silently discarded
/// (TODO.md #63). A real (if partial — only <c>Main</c>'s own signature is parsed) syntax tree instead
/// of a text/regex sniff, since the project already depends on Microsoft.CodeAnalysis.CSharp for
/// IntelliSense — a real AST doesn't false-positive on a commented-out old signature, a "Main" inside
/// a string literal, or a same-named member on some other local type. Parsed as raw top-level-statement
/// text (not wrapped in the <see cref="RpadKindTransform"/> class shell) — before compiling/analyzing,
/// the user's own `.rpad` code is still exactly what they typed, and `Main` (however it's later
/// compiled) is always written by the user as a bare local-function-shaped declaration, modifiers and
/// all — the parser accepts that syntax even though the binder would reject some of it standing alone
/// (e.g. `private` on a real local function is CS0106, a binder error, not a parse error), which is
/// exactly why this only ever needs a syntax-level check.
/// </summary>
internal static class ProgramEntryPointDetector
{
    // Found is false for a still-empty/Main-less script — IsStatic/ReturnsTask both default to false
    // in that case, matching RpadKindTransform's existing fallback (non-static, non-await call shape).
    public readonly record struct MainSignature(bool Found, bool IsStatic, bool ReturnsTask);

    public static MainSignature DetectMain(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var mainFunction = root.DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .FirstOrDefault(f => f.Identifier.Text == "Main");

        return mainFunction is null
            ? default
            : new MainSignature(
                Found: true,
                IsStatic: mainFunction.Modifiers.Any(m => m.RawKind == (int)SyntaxKind.StaticKeyword),
                ReturnsTask: IsTaskReturning(mainFunction.ReturnType));
    }

    private static bool IsTaskReturning(TypeSyntax returnType) => returnType switch
    {
        IdentifierNameSyntax { Identifier.Text: "Task" } => true,
        GenericNameSyntax { Identifier.Text: "Task" } => true,
        QualifiedNameSyntax q => IsTaskReturning(q.Right),
        _ => false,
    };
}
