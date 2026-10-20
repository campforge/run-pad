using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Runtime.UI.Tests;
using Xunit;

namespace RunPad.NonUI.Tests;

// Real compiles and runs (the installed SDK, a real dotnet process) of C# scripts written the way
// LINQPad accepts them: a Program with a classic entry point (a class with a static Main, usings
// and namespaces) as well as the LINQPad member style (a bare Main at the top level).
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class LinqPadCompatibilityRunTests
{
    [Fact]
    public async Task ImportedNamespaceProfile_Should_AllowGlobalUsingsInTheSource()
    {
        if (NoSdk) return;
        var doc = LinqConverter.Convert("""
            <Query Kind="Statements" />
            global using Text = System.Text;
            using System.Globalization;
            Console.WriteLine(new Text.StringBuilder("imports").ToString());
            """);
        var run = await RunAsync(doc.FrontMatter.Kind, doc.RunCode, usings: doc.FrontMatter.Namespaces);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.EndsWith("imports", run.Output);
    }

    private sealed record Run(RunResult Result, string Output, string Errors, string Dumps);

    private static async Task<Run> RunAsync(RpadKind kind, string code, IReadOnlyList<string>? args = null, bool allowUnsafe = false,
        string? outputDirectory = null, IReadOnlyList<string>? usings = null)
    {
        var outputDir = outputDirectory ?? Directory.CreateTempSubdirectory("runpad-linqpad-compat").FullName;
        try
        {
            var options = new CompiledDotnetLanguageHostOptions
            {
                Platform = new DotnetSdkProvider().Get().First(),
                ExecutionOptions = new ExecutionOptions
                {
                    WorkingDirectory = outputDir,
                    Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                    Debugger = new DebuggerOptions { DebuggerPath = "" },
                },
                OutputDirectory = outputDir,
                RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
                Kind = kind,
                Arguments = args ?? [],
                AllowUnsafe = allowUnsafe,
                Usings = usings ?? [],
            };
            await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
            var output = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            var dumps = new ConcurrentQueue<string>();
            // A Dump arrives as base64-encoded HTML.
            var result = await host.RunAsync(code, (data, kind) =>
                {
                    if (kind == OutputKind.DumpHtml) dumps.Enqueue(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)));
                    else (kind == OutputKind.StandardError ? errors : output).Enqueue(data);
                })
                .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            return new Run(result, string.Join('\n', output), string.Join('\n', errors), string.Join('\n', dumps));
        }
        finally
        {
            // A directory the caller passed is the caller's to remove.
            if (outputDirectory is null)
            {
                try { Directory.Delete(outputDir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private const string StructSizes = """
        // Struct sizes need unsafe code: sizeof of a user-defined struct.

        unsafe static void Main()
        {
            Console.WriteLine(sizeof(Pair));
            sizeof(Padded).Dump();
        }

        struct Pair
        {
            public int First;
            public int Second;
        }

        struct Padded
        {
            public byte Small;
            public long Large;
        }
        """;

    // With the script's unsafe setting on, sizeof of user structs compiles and runs (8 and 16 bytes).
    [Fact]
    public async Task UnsafeCode_WithTheSettingOn_Should_CompileAndRun()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, StructSizes, allowUnsafe: true);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.Equal("8", run.Output.Split('\n').Last(l => !l.Contains("warning", StringComparison.Ordinal)));
        Assert.Contains(">16<", run.Dumps);
    }

    // Off by default: the same script is refused with the compiler's CS0227 and does not run.
    [Fact]
    public async Task UnsafeCode_WithTheSettingOff_Should_FailWithCS0227()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, StructSizes);
        Assert.Equal(RunOutcome.CompilationFailed, run.Result.Outcome);
        Assert.Contains("CS0227", run.Errors);
        Assert.Empty(run.Dumps);
    }

    // Turning the setting on or off is a different build: an earlier binary is never reused for it.
    [Fact]
    public async Task ChangingTheUnsafeSetting_Should_NotReuseTheEarlierBuild()
    {
        if (NoSdk) return;
        var outputDir = Directory.CreateTempSubdirectory("runpad-linqpad-unsafe-cache").FullName;
        try
        {
            Assert.True((await RunAsync(RpadKind.Program, StructSizes, allowUnsafe: true, outputDirectory: outputDir)).Result.Succeeded);
            var off = await RunAsync(RpadKind.Program, StructSizes, allowUnsafe: false, outputDirectory: outputDir);
            Assert.Equal(RunOutcome.CompilationFailed, off.Result.Outcome);
            Assert.Contains("CS0227", off.Errors);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    private static bool NoSdk => !new DotnetSdkProvider().Get().Any();

    // A classic entry point and a helper in a class: the shape of a pre-C# 9 console program.
    [Fact]
    public async Task AProgramWithAClassAndStaticMain_Should_RunThatMain()
    {
        if (NoSdk) return;
        const string code = """
            // Converted with a comment above the class, as LINQPad files often are.

            class Program
            {
                static void Main()
                {
                    Console.WriteLine(ToInches(30));
                    Console.WriteLine(ToInches(100));
                }

                static int ToInches(int feet)
                {
                    var inches = feet * 12;
                    return inches;
                }
            }
            """;
        var run = await RunAsync(RpadKind.Program, code);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.Equal("360\n1200", run.Output);
    }

    // Using directives (plain, alias, static, global) before a namespace with the entry point class.
    [Fact]
    public async Task AProgramWithUsingsAndANamespace_Should_KeepThemAndRun()
    {
        if (NoSdk) return;
        const string code = """
            global using System.Text;
            using System;
            using Out = System.Console;
            using static System.Math;

            namespace Samples.Basics
            {
                class Entry
                {
                    static void Main()   // the entry point
                    {
                        int x = 12 * 30;
                        Out.WriteLine(x);
                        Out.WriteLine(new StringBuilder().Append(Abs(-7)).ToString());
                    }
                }
            }
            """;
        var run = await RunAsync(RpadKind.Program, code);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.Equal("360\n7", run.Output);
    }

    // The CLR's own entry point rules: args from the script and an awaited Task<int> as the exit code.
    [Fact]
    public async Task AClassicAsyncMainReturningInt_Should_GetArgs_AndSetTheExitCode()
    {
        if (NoSdk) return;
        const string code = """
            using System.Threading.Tasks;

            static class Program
            {
                static async Task<int> Main(string[] args)
                {
                    await Task.Yield();
                    Console.WriteLine(string.Join("|", args));
                    return 40 + args.Length;
                }
            }
            """;
        var run = await RunAsync(RpadKind.Program, code, ["one", "two"]);
        Assert.Equal(RunOutcome.Exited, run.Result.Outcome);
        Assert.Equal(42, run.Result.ExitCode);
        Assert.Equal("one|two", run.Output);
    }

    // Two candidate entry points: the compiler says so, and neither runs.
    [Fact]
    public async Task TwoClassicEntryPoints_Should_FailWithTheCompilersOwnError()
    {
        if (NoSdk) return;
        const string code = """
            class A { static void Main() => Console.WriteLine("A ran"); }
            class B { static void Main() => Console.WriteLine("B ran"); }
            """;
        var run = await RunAsync(RpadKind.Program, code);
        Assert.Equal(RunOutcome.CompilationFailed, run.Result.Outcome);
        Assert.Contains("CS0017", run.Errors);
        Assert.DoesNotContain("ran", run.Output);
    }

    // A Program with no entry point at all is still an error, not an empty success.
    [Theory]
    [InlineData("")]
    [InlineData("// nothing here yet\n")]
    [InlineData("class Helper { static void Run() => Console.WriteLine(\"ran\"); }")]
    public async Task AProgramWithoutAnyMain_Should_FailToCompile(string code)
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, code);
        Assert.Equal(RunOutcome.CompilationFailed, run.Result.Outcome);
        Assert.Contains("CS5001", run.Errors);
        Assert.DoesNotContain("ran", run.Output);
    }

    // Top-level code next to a classic class keeps the LINQPad meaning (members of the program); with
    // no top-level Main the compile fails instead of running the class's Main or the stray statement.
    [Fact]
    public async Task AStrayStatementBesideAClassicProgram_Should_NotRunEither()
    {
        if (NoSdk) return;
        const string code = """
            Console.WriteLine("stray");

            class Program { static void Main() => Console.WriteLine("class main"); }
            """;
        var run = await RunAsync(RpadKind.Program, code);
        Assert.Equal(RunOutcome.CompilationFailed, run.Result.Outcome);
        Assert.DoesNotContain("stray\n", run.Output + "\n");
        Assert.DoesNotContain("class main", run.Output.Replace("'Program.Main()'", ""));
    }

    // LINQPad member style is unchanged: a bare Main with fields and helpers around it.
    [Fact]
    public async Task AMemberStyleMain_Should_StillRun_WithArgsAndExitCode()
    {
        if (NoSdk) return;
        const string code = """
            int start = 12;

            int Main(string[] args)
            {
                Console.WriteLine(Times(30));
                return args.Length;
            }

            private int Times(int n) => start * n;
            """;
        var run = await RunAsync(RpadKind.Program, code, ["a", "b", "c"]);
        Assert.Equal(3, run.Result.ExitCode);
        Assert.Equal("360", run.Output);
    }

    // The entry point is the top-level Main, even when a helper holds a local function also named
    // Main with a signature RunPad would otherwise prefer.
    [Fact]
    public async Task AMainNestedInAHelper_Should_NotBecomeTheEntryPoint()
    {
        if (NoSdk) return;
        const string code = """
            void Main() => Console.WriteLine("top-level main");

            void Helper()
            {
                static int Main(string[] args) => 99;
                Main([]);
            }
            """;
        var run = await RunAsync(RpadKind.Program, code);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.Equal("top-level main", run.Output);
    }

    // A Statements script with nothing to run (a LINQPad readme is often only comments) exits
    // successfully without output instead of failing for a missing entry point.
    [Theory]
    [InlineData("")]
    [InlineData("// Welcome: this query only has comments.\n//\n// Nothing to run.\n")]
    [InlineData("// no newline at the end")]
    [InlineData("/* a block comment */\n")]
    [InlineData("using System.Text;\nusing Json = System.Text.Json.JsonSerializer;\nusing static System.Math;\n")]
    [InlineData("#define LOCAL\n#nullable enable\n// directives only\n")]
    [InlineData("#if NEVER_DEFINED\nConsole.WriteLine(\"excluded\");\n#endif\n")]
    public async Task AnEmptyStatementsScript_Should_SucceedWithoutOutput(string code)
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Statements, code);
        Assert.True(run.Result.Succeeded, run.Errors + run.Output);
        Assert.DoesNotContain("CS5001", run.Output + run.Errors);
        Assert.DoesNotContain("excluded", run.Output);
        Assert.Empty(run.Dumps);
    }

    // Code that only looks empty still fails as written: syntax errors, an unterminated comment, and
    // declarations without a statement (also ones present under the build's own symbols, like DEBUG).
    [Theory]
    [InlineData("Console.WriteLine(", "CS1026")]
    [InlineData("/* never closed\n", "CS1035")]
    [InlineData("#if NEVER_DEFINED\n// open\n", "CS1027")]
    [InlineData("class OnlyAType { }\n", "CS5001")]
    [InlineData("#if DEBUG\nclass DebugOnly { }\n#endif\n", "CS5001")]
    public async Task CodeThatOnlyLooksEmpty_Should_StillFail(string code, string expected)
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Statements, code);
        Assert.Equal(RunOutcome.CompilationFailed, run.Result.Outcome);
        Assert.Contains(expected, run.Errors);
        Assert.DoesNotContain("CS8803", run.Errors);
    }

    // How a Program is classified and called follows the build's own preprocessor symbols (DEBUG,
    // TRACE, the target framework's): code under a symbol the build defines counts, code under one it
    // doesn't is ignored, exactly as the compiler sees it.
    [Fact]
    public async Task AMemberMainUnderDebug_Should_Run()
    {
        if (NoSdk) return;
        const string code = "#if DEBUG\nvoid Main() { Console.WriteLine(\"EXPECTED-MAIN\"); }\n#endif\n";
        var run = await RunAsync(RpadKind.Program, code);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.Equal("EXPECTED-MAIN", run.Output);
    }

    [Fact]
    public async Task MemberAndClassicMains_UnderOppositeBranches_Should_RunTheDefinedOne_WithArgs()
    {
        if (NoSdk) return;
        const string member = "#if DEBUG\nvoid Main(string[] args) => Console.WriteLine(\"member:\" + args.Length);\n#else\nclass Program { static void Main() => Console.WriteLine(\"classic\"); }\n#endif\n";
        var run = await RunAsync(RpadKind.Program, member, ["a", "b"]);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.Equal("member:2", run.Output);

        const string classic = "#if !DEBUG\nvoid Main() => Console.WriteLine(\"member\");\n#else\nclass Program\n{\n    static async Task<int> Main(string[] args)\n    {\n        await Task.Yield();\n        Console.WriteLine(\"classic:\" + args[0]);\n        return 5;\n    }\n}\n#endif\n";
        var classicRun = await RunAsync(RpadKind.Program, classic, ["x"]);
        Assert.Equal(5, classicRun.Result.ExitCode);
        Assert.Equal("classic:x", classicRun.Output);
    }

    // The call shape (args, async, exit code) comes from the Main the build compiles.
    [Fact]
    public async Task TheMainSignature_Should_ComeFromTheDefinedBranch()
    {
        if (NoSdk) return;
        const string code = "#if TRACE\nasync Task<int> Main(string[] args)\n{\n    await Task.Yield();\n    Console.WriteLine(\"trace:\" + args.Length);\n    return 7;\n}\n#else\nvoid Main() => Console.WriteLine(\"other\");\n#endif\n";
        var run = await RunAsync(RpadKind.Program, code, ["1", "2", "3"]);
        Assert.Equal(7, run.Result.ExitCode);
        Assert.Equal("trace:3", run.Output);
    }

    [Fact]
    public async Task AMainUnderAFrameworkSymbol_Should_Run()
    {
        if (NoSdk) return;
        const string code = "#if NET8_0_OR_GREATER\nint Main() { Console.WriteLine(\"framework\"); return 4; }\n#endif\n";
        var run = await RunAsync(RpadKind.Program, code);
        Assert.Equal(4, run.Result.ExitCode);
        Assert.Equal("framework", run.Output);
    }

    // A Main excluded by an undefined symbol is not an entry point: the Program has none.
    [Fact]
    public async Task AMainUnderAnUndefinedSymbol_Should_NotRun()
    {
        if (NoSdk) return;
        const string code = "#if NEVER_DEFINED\nvoid Main() => Console.WriteLine(\"excluded\");\n#endif\n";
        var run = await RunAsync(RpadKind.Program, code);
        Assert.Equal(RunOutcome.CompilationFailed, run.Result.Outcome);
        Assert.Contains("CS5001", run.Errors);
        Assert.DoesNotContain("excluded\n", run.Output + "\n");
    }
    [Fact]
    public async Task ExtensionClasses_Should_RunOutsideTheMemberProgramWrapper()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, """
            void Main()
            {
                Console.WriteLine(21.Twice());
                Console.WriteLine(new Value { Number = 7 }.Read());
                int n = 2;
                n.Increment();
                Console.WriteLine(n);
                Console.WriteLine("abc".Identity());
            }
            static class NumberExtensions
            {
                public static int Twice(this int n) => n * 2;
                public static void Increment(this ref int n) => n++;
                public static T Identity<T>(this T value) where T : notnull => value;
            }
            internal class Value { public int Number; }
            static class ValueExtensions
            {
                public static int Read(this Value value) => value.Number;
            }
            """);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.EndsWith("42\n7\n3\nabc", run.Output);
    }

    // As in LINQPad, only static classes leave the generated class: other types stay nested and
    // keep using the script's own private members by their simple names.
    [Fact]
    public async Task ExtensionClasses_Should_LeaveOtherTypesNestedWithAccessToTheScript()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, """
            void Main() { new Doubler().Run(); }
            static int Twice(int x) => x * 2;
            class Doubler { public void Run() => Console.WriteLine(Twice(4).Squared()); }
            static class NumberExtensions { public static int Squared(this int n) => n * n; }
            """);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.EndsWith("64", run.Output);
    }

    // A moved class reaches the nested types and the script's members as LINQPad's do, through the
    // generated class, so they need internal or public access; a private one is a compile error.
    [Fact]
    public async Task ExtensionClasses_Should_UseNestedTypesAndMembers_ThatAreInternal()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, """
            void Main() => Console.WriteLine(new Point(3, 4).Length());
            internal static int Scale = 10;
            internal record Point(int X, int Y);
            static class PointExtensions { public static int Length(this Point p) => (p.X * p.X + p.Y * p.Y) * Scale; }
            """);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.EndsWith("250", run.Output);

        var hidden = await RunAsync(RpadKind.Program, """
            void Main() => Console.WriteLine(new Point(3, 4).Length());
            record Point(int X, int Y);
            static class PointExtensions { public static int Length(this Point p) => p.X + p.Y; }
            """);
        Assert.Equal(RunOutcome.CompilationFailed, hidden.Result.Outcome);
    }

    [Fact]
    public async Task ExtensionAwaiter_Should_BeAwaitedByAsyncMain()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, """
            #if DEBUG
            async Task<int> Main(string[] args)
            {
                await TimeSpan.Zero;
                Console.WriteLine(args[0]);
                return 17;
            }
            static class AwaiterExtensions
            {
                public static System.Runtime.CompilerServices.TaskAwaiter GetAwaiter(this TimeSpan duration)
                    => Task.Delay(duration).GetAwaiter();
            }
            #endif
            """, args: ["awaited"]);
        Assert.Equal(17, run.Result.ExitCode);
        Assert.EndsWith("awaited", run.Output);
    }

    [Fact]
    public async Task ExtensionClassesInsideNamespaces_Should_RemainAtNamespaceScope()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, """
            void Main() { Console.WriteLine(Tools.Runner.Compute()); }
            namespace Tools
            {
                public static class Runner { public static int Compute() => 5.Twice(); }
                static class Extensions { public static int Twice(this int n) => n * 2; }
            }
            """);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.EndsWith("10", run.Output);
    }

    [Fact]
    public async Task CSharp14ExtensionBlocks_Should_RemainAtNamespaceScope()
    {
        if (NoSdk) return;
        var run = await RunAsync(RpadKind.Program, """
            void Main() { Console.WriteLine(4.Twice()); }
            static class Extensions
            {
                extension(int number)
                {
                    public int Twice() => number * 2;
                }
            }
            """);
        Assert.True(run.Result.Succeeded, run.Errors);
        Assert.EndsWith("8", run.Output);
    }

}
