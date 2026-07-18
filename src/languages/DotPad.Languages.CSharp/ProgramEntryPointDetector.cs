using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotPad.Languages.CSharp;

/// <summary>
/// Whether a <see cref="DotPad.Format.RPad.RpadKind.Program"/> script's <c>Main</c> local function is
/// Task-returning (<c>async Task Main()</c>/<c>Task Main()</c>/generic <c>Task&lt;T&gt;</c> forms) —
/// <see cref="RpadKindTransform"/> needs this to decide between <c>"Main();"</c> and
/// <c>"await Main();"</c>, so an async Main's work (and any exception thrown inside it) actually gets
/// awaited instead of silently discarded as a fire-and-forget call from a top-level statement
/// (TODO.md #63). A real (if partial — only <c>Main</c>'s own signature is parsed) syntax tree instead
/// of a text/regex sniff, since the project already depends on Microsoft.CodeAnalysis.CSharp for
/// IntelliSense — a real AST doesn't false-positive on a commented-out old signature, a "Main" inside
/// a string literal, or a same-named member on some other local type.
/// </summary>
internal static class ProgramEntryPointDetector
{
    public static bool MainReturnsTask(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var mainFunction = root.DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .FirstOrDefault(f => f.Identifier.Text == "Main");

        return mainFunction is not null && IsTaskReturning(mainFunction.ReturnType);
    }

    private static bool IsTaskReturning(TypeSyntax returnType) => returnType switch
    {
        IdentifierNameSyntax { Identifier.Text: "Task" } => true,
        GenericNameSyntax { Identifier.Text: "Task" } => true,
        QualifiedNameSyntax q => IsTaskReturning(q.Right),
        _ => false,
    };
}
