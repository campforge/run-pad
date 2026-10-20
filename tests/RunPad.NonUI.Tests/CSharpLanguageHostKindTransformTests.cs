using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// CalcKindOffset() returned 2 for RpadKind.Program, but ApplyKindTransform's
// Program branch ("Main();{code}", no newline) glued onto the same physical line as the user's first
// line — structurally zero new lines, same as Statements' passthrough — which misaligned every
// breakpoint/stack frame in Program-kind scripts by exactly the erroneous offset.
//
// The "Main();{code}" shape itself turned out to be its own bug — it left
// the user's whole script as raw top-level statements, which broke as soon as a Program-kind script had
// more than just Main (fields declared below Main became CS0165 "unassigned", methods with an access
// modifier became CS0106 "not valid for this item" — LINQPad-style scripts, the norm for this kind, hit
// both routinely). ApplyKindTransform's Program branch now wraps the user's code in a real class
// instead (RpadKindTransform.ProgramPrefixFor/ProgramSuffixFor) — field initializers become constructor
// prologue (always runs before any instance method call, regardless of textual order) and ordinary
// access modifiers are exactly what class members expect. That shape needs 2 lines of offset (the
// wrapper class's header + opening brace), not 0 — CalcKindOffset changed accordingly. A real
// dotnet-build integration test (CSharpLanguageHostProgramKindCompileTests) caught a second, subtler
// issue the string-pinning tests below don't by themselves prove fixed: the user's own Main, written
// with no modifier (the LINQPad-style norm), is private as a class member — uncallable from the
// top-level-statement call site outside the class (CS0122). ProgramSuffixFor appends a synthetic
// trampoline INSIDE the class (same-class access ignores accessibility) so the outer call site never
// touches Main directly.
//
// These tests pin the exact transformed text and offset for all three kinds directly against the real
// (internal) CSharpLanguageHost members, rather than only inferring correctness from DapSession's line
// math.
public class CSharpLanguageHostKindTransformTests
{
    // CSharpLanguageHost only implements IAsyncDisposable (see LanguageHostBase) — nothing here starts
    // a process or opens a handle (only Directory.CreateDirectory in the ctor for OutputDirectory), so
    // these tests don't bother disposing; the temp directory is cleaned up explicitly instead.
    private static CSharpLanguageHost CreateHost(RpadKind kind, out string outputDir, IReadOnlyList<string>? usings = null)
    {
        outputDir = Path.Combine(Path.GetTempPath(), "runpad-tests-" + Guid.NewGuid());
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk("net10.0", "net10.0", "10.0.0", "net10.0"),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "netcoredbg" },
            },
            OutputDirectory = outputDir,
            RuntimeLibPath = "RunPad.Runtime.dll",
            Kind = kind,
            Usings = usings ?? [],
        };
        return new CSharpLanguageHost(options, NullLoggerFactory.Instance);
    }

    private const string WrapperClass = RpadKindTransform.ProgramWrapperClassName;
    private const string TrampolineCall = $"{WrapperClass}.__RunPadRun(args)";

    // The trampoline takes the top-level `args`; an int-returning Main's result is returned from the
    // top level (the exit code).
    private static string ExpectedProgramTransform(string code, bool isStatic, bool returnsTask, bool returnsInt = false)
    {
        var call = returnsTask ? $"await {TrampolineCall}" : TrampolineCall;
        var callStatement = returnsInt ? $"return {call};" : $"{call};";
        var mainCall = isStatic ? "Main()" : $"new {WrapperClass}().Main()";
        var returnType = (returnsTask, returnsInt) switch
        {
            (true, true) => "global::System.Threading.Tasks.Task<int>",
            (true, false) => "global::System.Threading.Tasks.Task",
            (false, true) => "int",
            _ => "void",
        };
        var trampoline = returnType == "void"
            ? $"internal static void __RunPadRun(string[] args) {{ {mainCall}; }}"
            : $"internal static {returnType} __RunPadRun(string[] args) => {mainCall};";
        return $"{callStatement}class {WrapperClass}\n{{\n{code}\n{trampoline}\n}}\n";
    }

    [Theory]
    [InlineData(RpadKind.Statements, "int x = 1;", "int x = 1;")]
    [InlineData(RpadKind.Expression, "1 + 1", "(\n1 + 1\n).Dump();\n")]
    public void ApplyKindTransform_Should_ProduceExpectedText(RpadKind kind, string code, string expected)
    {
        var host = CreateHost(kind, out var outputDir);
        try
        {
            Assert.Equal(expected, host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public void ApplyKindTransform_Should_WrapProgramCodeInAClass_WithACallGluedOntoTheFirstLine()
    {
        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            const string code = "void Main() { }";
            Assert.Equal(ExpectedProgramTransform(code, isStatic: false, returnsTask: false), host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // The exact reported repro: a field declared below Main, and a helper method with
    // an access modifier — both CS0165 and CS0106 under the old raw-top-level-statements transform.
    // Not a compile check (that's CSharpLanguageHostProgramKindCompileTests, a real dotnet-build
    // integration test) — just confirms the transform actually produces the class-wrapped shape that
    // makes both legal.
    [Fact]
    public void ApplyKindTransform_Should_PlaceFieldsAndModifiedMethods_InsideTheWrapperClass_ForProgramKind()
    {
        const string code = "Regex regexp = new Regex(@\"_(?<resolution>(\\d+)x(\\d+))\\.\");\n\n"
            + "async Task Main(){\n\tvar filename = \"100X100_123\";\n\tGetResolution(filename).Dump();\n}\n\n"
            + "private string? GetResolution(string fileName)\n{\n"
            + "    return regexp.Matches(fileName).Cast<Match>().ToList().FirstOrDefault()?.Groups[\"resolution\"].Value;\n}\n";

        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            var transformed = host.ApplyKindTransform(code);
            Assert.StartsWith($"await {TrampolineCall};class {WrapperClass}\n{{\n", transformed, StringComparison.Ordinal);
            Assert.EndsWith("\n}\n", transformed, StringComparison.Ordinal);
            // The whole user script, verbatim, sits between the class header and the synthetic
            // trampoline/closing brace — i.e. it's the class BODY now, not a sequence of top-level
            // statements the call was glued in front of.
            Assert.Contains(code, transformed, StringComparison.Ordinal);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Theory]
    [InlineData(RpadKind.Statements, 0)]
    [InlineData(RpadKind.Program, 2)]
    [InlineData(RpadKind.Expression, 1)]
    public void CalcKindOffset_Should_MatchLinesActuallyInsertedByApplyKindTransform(RpadKind kind, int expectedOffset)
    {
        var host = CreateHost(kind, out var outputDir);
        try
        {
            Assert.Equal(expectedOffset, host.CalcKindOffset("void Main() { }"));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Cross-check tying the two together: for every kind, the number of '\n' characters that appear
    // BEFORE the user's own code in the transformed text must equal CalcKindOffset() — this is the
    // actual invariant DapSession relies on (see its _lineOffset doc comment), not just two isolated
    // hardcoded expectations that could coincidentally both be updated wrong in the same way.
    [Theory]
    [InlineData(RpadKind.Statements)]
    [InlineData(RpadKind.Program)]
    [InlineData(RpadKind.Expression)]
    public void CalcKindOffset_Should_EqualNewlinesInsertedBeforeUserCodeByApplyKindTransform(RpadKind kind)
    {
        const string code = "FIRST_USER_LINE";
        var host = CreateHost(kind, out var outputDir);
        try
        {
            var transformed = host.ApplyKindTransform(code);
            var indexOfUserCode = transformed.IndexOf(code, StringComparison.Ordinal);
            Assert.True(indexOfUserCode >= 0);

            var newlinesBeforeUserCode = transformed[..indexOfUserCode].Count(c => c == '\n');
            Assert.Equal(newlinesBeforeUserCode, host.CalcKindOffset(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Follow-up regression: DotnetProcessCompiler.Compilation separately prepends one "using X;" line
    // per front-matter namespace BEFORE writing the .cs file that's actually compiled/debugged (its
    // own "Prepend global usings" step) — a real, kind-independent source of line drift that
    // CalcKindOffset previously ignored entirely, for every kind, not just Program. Reported live by
    // the user immediately after the kind-transform-only fix above ("now the debugger misses by
    // exactly one line") on a script whose front matter has one namespace configured.
    [Theory]
    [InlineData(RpadKind.Statements, 0)]
    [InlineData(RpadKind.Program, 2)]
    [InlineData(RpadKind.Expression, 1)]
    public void CalcKindOffset_Should_IncludeFrontMatterUsingsCount(RpadKind kind, int kindOnlyOffset)
    {
        IReadOnlyList<string> usings = ["System.Text", "System.Linq"];
        var host = CreateHost(kind, out var outputDir, usings);
        try
        {
            Assert.Equal(usings.Count + kindOnlyOffset, host.CalcKindOffset("void Main() { }"));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // The Program-kind call statement used to always be "Main();" — a
    // fire-and-forget call that discards a Task-returning Main's result, silently losing any exception
    // thrown inside it. ApplyKindTransform now picks between a plain and an awaited call to the
    // trampoline based on Main's own declared return type (the trampoline itself decides how to reach
    // Main — see ApplyKindTransform_Should_CallStaticMain_ByTypeName_NotByNewingUpAnInstance below).
    [Theory]
    [InlineData("void Main() { }", false)]
    [InlineData("async Task Main() { }", true)]
    [InlineData("Task Main() { return Task.CompletedTask; }", true)]
    [InlineData("async Task<int> Main() { return 0; }", true)]
    [InlineData("async System.Threading.Tasks.Task Main() { }", true)]
    public void ApplyKindTransform_Should_AwaitMain_OnlyWhenMainIsTaskReturning(string code, bool expectAwait)
    {
        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            Assert.Equal(ExpectedProgramTransform(code, isStatic: false, returnsTask: expectAwait, returnsInt: code.Contains("<int>")), host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // A static Main can't be called through an instance reference (CS0176) — the
    // synthetic trampoline needs a type-qualified call instead of `new {Wrapper}()` for it.
    [Theory]
    [InlineData("static void Main() { }", false)]
    [InlineData("static async Task Main() { }", true)]
    public void ApplyKindTransform_Should_CallStaticMain_ByTypeName_NotByNewingUpAnInstance(string code, bool expectAwait)
    {
        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            Assert.Equal(ExpectedProgramTransform(code, isStatic: true, returnsTask: expectAwait), host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Regression guard: a real AST (not a text/regex sniff) shouldn't be fooled by "Main" showing up
    // somewhere that isn't the actual entry point's signature.
    [Fact]
    public void ApplyKindTransform_Should_NotAwaitMain_WhenTaskMainOnlyAppearsInAComment()
    {
        const string code = "// async Task Main() { }\nvoid Main() { }";
        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            Assert.StartsWith($"{TrampolineCall};", host.ApplyKindTransform(code), StringComparison.Ordinal);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // A program without top-level statements (a class with a static Main, usings, namespaces) is
    // compiled exactly as written: no wrapper, no line offset beyond the front-matter usings.
    [Theory]
    [InlineData("class Program\n{\n    static void Main() { }\n}\n")]
    [InlineData("using System;\nusing Con = System.Console;\nusing static System.Math;\nglobal using System.Text;\n\nnamespace N\n{\n    class A { static void Main() { } }\n}\n")]
    [InlineData("// only a comment\n")]
    [InlineData("")]
    public void ApplyKindTransform_Should_LeaveAProgramWithoutTopLevelStatements_AsWritten(string code)
    {
        var host = CreateHost(RpadKind.Program, out var outputDir, ["System.Text"]);
        try
        {
            Assert.Equal(code, host.ApplyKindTransform(code));
            Assert.Equal(1, host.CalcKindOffset(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // The entry point of a LINQPad-style program is a Main written at the top level only; a local
    // function named Main inside a helper must not decide how the program is called.
    [Fact]
    public void ApplyKindTransform_Should_IgnoreAMainNestedInAHelper()
    {
        const string code = "void Main() { }\n\nvoid Helper()\n{\n    static async Task<int> Main(string[] args) => 0;\n}\n";
        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            Assert.Equal(ExpectedProgramTransform(code, isStatic: false, returnsTask: false), host.ApplyKindTransform(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // The call statement itself is always a single physical line with no embedded '\n' — CalcKindOffset
    // relies on the wrapper class's header/opening-brace lines (exactly 2) being the only source of the
    // offset, regardless of which call-statement variant ApplyKindTransform actually picked.
    [Theory]
    [InlineData("void Main() { }")]
    [InlineData("async Task Main() { }")]
    [InlineData("static void Main() { }")]
    public void CalcKindOffset_Should_StayAtTwoForProgram_RegardlessOfWhichMainCallVariantWasUsed(string code)
    {
        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            var transformed = host.ApplyKindTransform(code);
            var callStatementEnd = transformed.IndexOf("class " + WrapperClass, StringComparison.Ordinal);
            Assert.True(callStatementEnd > 0);
            Assert.DoesNotContain('\n', transformed[..callStatementEnd]);
            Assert.Equal(2, host.CalcKindOffset(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // An empty Statements script gets a lone `return;` after the user's code, on its own line: the
    // user's text, and with it every offset, is unchanged. Anything else stays exactly as written.
    [Theory]
    [InlineData("", "return;\n")]
    [InlineData("// note", "// note\nreturn;\n")]
    [InlineData("using System.Text;\n", "using System.Text;\nreturn;\n")]
    [InlineData("Console.WriteLine(1);\n", "Console.WriteLine(1);\n")]
    [InlineData("class C { }\n", "class C { }\n")]
    [InlineData("Console.WriteLine(", "Console.WriteLine(")]
    public void ApplyKindTransform_Should_GiveOnlyAnEmptyStatementsScript_AReturn(string code, string expected)
    {
        var host = CreateHost(RpadKind.Statements, out var outputDir);
        try
        {
            var transformed = host.ApplyKindTransform(code);
            Assert.Equal(expected, transformed);
            Assert.StartsWith(code, transformed, StringComparison.Ordinal);
            Assert.Equal(0, host.CalcKindOffset(code));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Classification uses the build's preprocessor symbols: a member Main under DEBUG gets the wrapper
    // and its 2-line offset; one under an undefined symbol leaves a program with no top-level code.
    [Theory]
    [InlineData("#if DEBUG\nvoid Main() { }\n#endif\n", 2)]
    [InlineData("#if TRACE\nasync Task<int> Main(string[] args) => 0;\n#endif\n", 2)]
    [InlineData("#if NEVER_DEFINED\nvoid Main() { }\n#endif\n", 0)]
    [InlineData("#if !DEBUG\nvoid Main() { }\n#else\nclass P { static void Main() { } }\n#endif\n", 0)]
    public void Classification_Should_FollowTheBuildsSymbols(string code, int offset)
    {
        var host = CreateHost(RpadKind.Program, out var outputDir);
        try
        {
            var transformed = host.ApplyKindTransform(code);
            Assert.Equal(offset, host.CalcKindOffset(code));
            Assert.Equal(offset, transformed[..transformed.IndexOf(code, StringComparison.Ordinal)].Count(c => c == '\n'));
            Assert.Equal(offset == 2, transformed.Contains(WrapperClass, StringComparison.Ordinal));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
