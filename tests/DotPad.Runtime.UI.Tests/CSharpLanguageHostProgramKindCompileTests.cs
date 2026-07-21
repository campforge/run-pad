using DotPad.Build.Core.Models;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #107 — live user report: a Program-kind script with more than a bare Main (the LINQPad-style
// norm for this kind — a field plus Main plus a helper method) failed to compile at all. This is a real
// dotnet-build integration test (no mocking of the compiler, same rationale/pattern as
// DotnetProcessCompilerTests) — the bug was entirely about what MSBuild/Roslyn actually accept, which
// pure string-pinning against ApplyKindTransform's output (CSharpLanguageHostKindTransformTests) can't
// by itself prove fixed; that file pins the transform's exact shape, this one proves the shape compiles.
public class CSharpLanguageHostProgramKindCompileTests
{
    private static CSharpLanguageHost CreateHost(string outputDir, IReadOnlyList<string>? usings = null)
    {
        var sdk = new DotnetSdkProvider().Get().First(); // real installed SDK, same resolution as production
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = sdk,
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = outputDir,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = RpadKind.Program,
            Usings = usings ?? [],
        };
        return new CSharpLanguageHost(options, NullLoggerFactory.Instance);
    }

    // The exact repro from the live report: a field (Regex regexp) declared BELOW where it's used
    // syntactically relative to Main, plus a helper method with an explicit access modifier. Under the
    // old "raw top-level statements + a glued-on Main(); call" transform this failed with CS0165 (the
    // field observed as unassigned inside Main's closure, since the synthesized call ran before the
    // field's own declaration statement) AND CS0106 (private is not valid on a local function).
    // System.Text.RegularExpressions comes from front-matter Usings (the way a real .rpad script would
    // supply it) rather than an inline `using` in the code block — a `using` directive can't appear
    // inside a class body, which the user's code becomes here; it never could have appeared as the
    // first line of Program-kind code either, even before this fix (the old transform glued "Main();"
    // in front of it, and a using directive must precede everything else in the file).
    [Fact]
    public async Task CompileAsync_Should_Succeed_ForALinqPadStyleScript_WithAFieldBelowMain_AndAModifiedHelperMethod()
    {
        const string code = """
            Regex regexp = new Regex(@"_(?<resolution>(\d+)x(\d+))\.");

            async Task Main(){
                var filename = "100X100_123";
                GetResolution(filename).Dump();
            }

            private string? GetResolution(string fileName)
            {
                return regexp.Matches(fileName).Cast<Match>().ToList().FirstOrDefault()?.Groups["resolution"].Value;
            }
            """;

        var outputDir = Directory.CreateTempSubdirectory("dotpad-program-kind-test").FullName;
        try
        {
            await using var host = CreateHost(outputDir, usings: ["System.Text.RegularExpressions"]);
            ICompilationResult result = await host.CompileAsync(code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Diagnostics.Select(d => $"{d.Code} {d.File}:{d.Line} {d.Message}")));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // A static Main is legal LINQPad-style C# too (no instance state needed) — must be called by type
    // name, not through `new {Wrapper}()` (CS0176), which is exactly the other half of TODO.md #107's
    // fix (ProgramEntryPointDetector.DetectMain.IsStatic).
    [Fact]
    public async Task CompileAsync_Should_Succeed_ForAStaticMain_WithAPrivateHelperMethod()
    {
        const string code = """
            static void Main()
            {
                Greet("world").Dump();
            }

            private static string Greet(string name) => $"hello {name}";
            """;

        var outputDir = Directory.CreateTempSubdirectory("dotpad-program-kind-static-test").FullName;
        try
        {
            await using var host = CreateHost(outputDir);
            ICompilationResult result = await host.CompileAsync(code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Diagnostics.Select(d => $"{d.Code} {d.File}:{d.Line} {d.Message}")));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Regression guard for the OLD behavior that DID work and must keep working: a Program-kind script
    // containing nothing but Main itself (no fields, no extra methods) — the case the old transform was
    // actually built for.
    [Fact]
    public async Task CompileAsync_Should_StillSucceed_ForABareMain_WithNoFieldsOrHelperMethods()
    {
        const string code = "void Main()\n{\n    \"hello\".Dump();\n}\n";

        var outputDir = Directory.CreateTempSubdirectory("dotpad-program-kind-bare-test").FullName;
        try
        {
            await using var host = CreateHost(outputDir);
            ICompilationResult result = await host.CompileAsync(code, CancellationToken.None);
            Assert.True(result.Success, string.Join('\n', result.Diagnostics.Select(d => $"{d.Code} {d.File}:{d.Line} {d.Message}")));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
