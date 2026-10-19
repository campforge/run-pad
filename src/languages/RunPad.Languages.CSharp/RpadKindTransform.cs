using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RunPad.Languages.CSharp;

// RpadKind.Program used to glue a bare "Main();"/"await Main();" call directly in front
// of the user's own code, leaving everything (Main, and whatever else the user wrote alongside it) as
// C# top-level statements. That only worked for scripts containing nothing but Main itself — LINQPad-
// style scripts (field(s) + Main + helper methods, the norm for this kind, not the exception) hit two
// independent compile failures the moment they have more than that: a method with an access modifier
// becomes CS0106 (local functions only accept static/async/unsafe/extern, never public/private/
// protected/internal), and a field declared textually below Main becomes CS0165 (Main, glued in as the
// very first statement, runs before the field's own declaration statement ever executes, so any local
// function closing over it observes it as unassigned).
//
// Fix: wrap the user's Program-kind code in a real class instead. Field initializers become constructor
// prologue — they always finish before ANY instance method call, regardless of the field's textual
// position relative to Main — and ordinary access modifiers are exactly what class members expect.
//
// The user's own Main is (as usual for a LINQPad-style script) written with no access modifier at all,
// which for a class member means private — so it can't be called directly from the top-level-statement
// call site the entry point needs to live at, that's OUTSIDE the class (CS0122, caught by a real
// dotnet-build integration test, not just string-pinning, before this comment was written). Instead a
// synthetic trampoline (TrampolineMethodName, itself always accessible — its own modifier is ours to
// pick) is appended INSIDE the class, after the user's own code — same-class member access ignores
// accessibility entirely, so the trampoline can call Main() (or `new {Wrapper}().Main()`, for a
// non-static Main) regardless of what the user wrote. The outer, single-line-glued call statement
// (ProgramPrefixFor) targets the trampoline, never Main directly.
//
// ProgramPrefixFor's call statement is glued onto the very first physical line, immediately followed by
// the class declaration's own header and opening brace on their own lines. CSharpLanguageHost.
// CalcKindOffset counts exactly those two embedded '\n's (the call statement itself is always a single
// physical line — the class header/brace are the only newline-bearing part, appearing identically
// regardless of whether Main turned out to be Task-returning). CSharpIntelliSenseService.SyncDocument
// mirrors both this prefix AND ProgramSuffixFor (the trampoline plus the class's closing brace) in its
// live-analysis document, so the two can't drift apart again — without mirroring the prefix, Roslyn
// correctly (from its own point of view) flagged the user's Main as an unused local function (CS8321);
// without mirroring the suffix, the live document would contain a permanently-open class declaration,
// breaking parsing/binding for the rest of the file.
internal static class RpadKindTransform
{
    // Double-underscore prefix to keep a collision with a user-declared member of the same name
    // astronomically unlikely — neither identifier is ever shown to the user (they exist only in the
    // throwaway .cs file actually handed to `dotnet build`/Roslyn, never in the .rpad source itself).
    public const string ProgramWrapperClassName = "__RunPadProgram";
    private const string TrampolineMethodName = "__RunPadRun";

    // Cancelled when the user presses Stop; RunPad then waits a moment for the script to finish.
    private const string CancellationTokenExpression = "global::Util.CancellationToken";

    // Glued onto one physical line, immediately followed by the wrapper class's header and opening
    // brace, each on its own line: exactly 2 embedded '\n's, regardless of whether Main turned out to
    // be Task-returning (ProgramLineOffset relies on this).
    // Always calls the trampoline (see class doc comment for why, not Main directly) — statically, so
    // it never needs `new`/an IsStatic check here; that check lives in ProgramSuffixFor instead, where
    // the trampoline itself decides how to reach the user's actual Main.
    // The top-level `args` are the script's command-line arguments; an int-returning Main's result is
    // returned from the top level, which makes it the process exit code.
    // Both are empty for a program compiled as written (ProgramEntryPointDetector.IsCompiledAsWritten).
    public static string ProgramPrefixFor(string code)
    {
        if (ProgramEntryPointDetector.IsCompiledAsWritten(code)) return "";
        var main = ProgramEntryPointDetector.DetectMain(code);
        var call = $"{ProgramWrapperClassName}.{TrampolineMethodName}(args)";
        if (main.ReturnsTask) call = "await " + call;
        var callStatement = main.ReturnsInt ? $"return {call};" : $"{call};";
        return $"{callStatement}class {ProgramWrapperClassName}\n{{\n";
    }

    // Appends the trampoline (always accessible from ProgramPrefixFor's call site, regardless of the
    // user's own Main having no modifier/being private) and closes the class ProgramPrefixFor opened.
    // Own leading '\n' so the user's own last line (even one with no trailing newline of its own, e.g.
    // a file that doesn't end in a blank line) isn't glued onto the same physical line as the
    // trampoline — keeps every synthetic line, like the prefix's, off of any line a breakpoint could
    // legitimately target (all of which sit strictly before this suffix begins).
    public static string ProgramSuffixFor(string code)
    {
        if (ProgramEntryPointDetector.IsCompiledAsWritten(code)) return "";
        var main = ProgramEntryPointDetector.DetectMain(code);
        var arguments = main.Parameters switch
        {
            ProgramEntryPointDetector.MainParameters.Args => "args",
            ProgramEntryPointDetector.MainParameters.Token => CancellationTokenExpression,
            ProgramEntryPointDetector.MainParameters.ArgsAndToken => $"args, {CancellationTokenExpression}",
            _ => "",
        };
        var mainCall = main.IsStatic ? $"Main({arguments})" : $"new {ProgramWrapperClassName}().Main({arguments})";
        var returnType = (main.ReturnsTask, main.ReturnsInt) switch
        {
            (true, true) => "global::System.Threading.Tasks.Task<int>",
            (true, false) => "global::System.Threading.Tasks.Task",
            (false, true) => "int",
            _ => "void",
        };
        // internal (not private): unlike Main, the trampoline IS called from outside the class — the
        // top-level-statement call site ProgramPrefixFor glues in front of the class declaration.
        var trampoline = returnType == "void"
            ? $"internal static void {TrampolineMethodName}(string[] args) {{ {mainCall}; }}"
            : $"internal static {returnType} {TrampolineMethodName}(string[] args) => {mainCall};";
        return $"\n{trampoline}\n}}\n";
    }

    // A Statements script without a single statement or declaration (empty, comments, using
    // directives or preprocessor lines only) has nothing to run, and C# would refuse it for lacking
    // an entry point (CS5001). It gets one `return;` on its own line after the user's code, so it runs
    // and exits successfully without output; every original offset stays where it was. Code with a
    // syntax error, or with any statement, type or namespace, is compiled exactly as written — a lone
    // type declaration still fails as it should. Parsed with the build's own preprocessor symbols, so
    // code inside `#if DEBUG` counts.
    public static string StatementsFor(string code, CSharpParseOptions parseOptions)
    {
        var tree = CSharpSyntaxTree.ParseText(code, parseOptions);
        if (((CompilationUnitSyntax)tree.GetRoot()).Members.Count > 0
            || tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return code;
        return code + (code.Length == 0 || code.EndsWith('\n') ? "" : "\n") + "return;\n";
    }

    /// <summary>Lines <see cref="ProgramPrefixFor"/> puts before the user's first line.</summary>
    public static int ProgramLineOffset(string code) => ProgramEntryPointDetector.IsCompiledAsWritten(code) ? 0 : 2;
}
