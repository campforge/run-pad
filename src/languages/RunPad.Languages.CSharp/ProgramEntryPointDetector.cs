using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RunPad.Languages.CSharp;

/// <summary>
/// Inspects a <see cref="RunPad.Format.RPad.RpadKind.Program"/> script's <c>Main</c> local function
/// signature — <see cref="RpadKindTransform"/> needs both facts captured in <see cref="MainSignature"/>
/// to build the right call statement: <c>IsStatic</c> picks between <c>__RunPadProgram.Main()</c> and
/// <c>new __RunPadProgram().Main()</c> (a static local function can't be called through an instance
/// reference — CS0176), and <c>ReturnsTask</c> (<c>async Task Main()</c>/<c>Task Main()</c>/generic
/// <c>Task&lt;T&gt;</c> forms) picks between a fire-and-forget call and an awaited one, so an async
/// Main's work (and any exception thrown inside it) actually gets awaited instead of silently discarded
///. A real (if partial — only <c>Main</c>'s own signature is parsed) syntax tree instead
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
    /// <summary>What <c>Main</c> takes: nothing, the command-line arguments, a token that is cancelled
    /// when the user presses Stop (<c>Util.CancellationToken</c>), or both.</summary>
    public enum MainParameters { None, Args, Token, ArgsAndToken }

    // Found is false for a still-empty/Main-less script — every other member then has its default,
    // matching RpadKindTransform's fallback (non-static, no-argument, no-await call shape).
    // ReturnsInt: int or Task<int>, which becomes the process exit code.
    public readonly record struct MainSignature(
        bool Found, bool IsStatic, bool ReturnsTask, bool ReturnsInt = false, MainParameters Parameters = MainParameters.None);

    // When a script declares several Main overloads, the one taking the most is called: arguments
    // and a cancellation token over a token alone, over arguments alone, over none. A Main with a
    // parameter list RunPad can't supply is only picked when it's the only one — the compiler then
    // reports why it can't be called.
    public static MainSignature DetectMain(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var mains = root.DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .Where(f => f.Identifier.Text == "Main")
            .ToList();
        if (mains.Count == 0) return default;

        var chosen = mains
            .Select(f => (Function: f, Parameters: ParametersOf(f)))
            .Where(m => m.Parameters is not null)
            .OrderByDescending(m => m.Parameters)
            .Select(m => ((LocalFunctionStatementSyntax Function, MainParameters Parameters)?)(m.Function, m.Parameters!.Value))
            .FirstOrDefault() ?? (mains[0], MainParameters.None);

        return new MainSignature(
            Found: true,
            IsStatic: chosen.Function.Modifiers.Any(m => m.RawKind == (int)SyntaxKind.StaticKeyword),
            ReturnsTask: IsTaskReturning(chosen.Function.ReturnType),
            ReturnsInt: IsIntReturning(chosen.Function.ReturnType),
            Parameters: chosen.Parameters);
    }

    // Null for a parameter list RunPad doesn't know how to fill.
    private static MainParameters? ParametersOf(LocalFunctionStatementSyntax main)
    {
        var types = main.ParameterList.Parameters.Select(p => p.Type).ToList();
        return types switch
        {
            [] => MainParameters.None,
            [var a] when IsStringArray(a) => MainParameters.Args,
            [var a] when IsCancellationToken(a) => MainParameters.Token,
            [var a, var b] when IsStringArray(a) && IsCancellationToken(b) => MainParameters.ArgsAndToken,
            _ => null,
        };
    }

    private static bool IsStringArray(TypeSyntax? type) => type is ArrayTypeSyntax
    {
        ElementType: PredefinedTypeSyntax { Keyword.Text: "string" } or IdentifierNameSyntax { Identifier.Text: "String" }
                     or QualifiedNameSyntax { Right.Identifier.Text: "String" },
        RankSpecifiers.Count: 1,
    };

    private static bool IsCancellationToken(TypeSyntax? type) => type switch
    {
        IdentifierNameSyntax { Identifier.Text: "CancellationToken" } => true,
        QualifiedNameSyntax q => q.Right.Identifier.Text == "CancellationToken",
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text == "CancellationToken",
        _ => false,
    };

    private static bool IsTaskReturning(TypeSyntax returnType) => returnType switch
    {
        IdentifierNameSyntax { Identifier.Text: "Task" } => true,
        GenericNameSyntax { Identifier.Text: "Task" } => true,
        QualifiedNameSyntax q => IsTaskReturning(q.Right),
        _ => false,
    };

    // int, or Task<int>.
    private static bool IsIntReturning(TypeSyntax returnType) => returnType switch
    {
        PredefinedTypeSyntax { Keyword.Text: "int" } => true,
        IdentifierNameSyntax { Identifier.Text: "Int32" } => true,
        GenericNameSyntax { Identifier.Text: "Task", TypeArgumentList.Arguments: [var argument] } => IsIntReturning(argument),
        QualifiedNameSyntax q => IsIntReturning(q.Right),
        _ => false,
    };
}
