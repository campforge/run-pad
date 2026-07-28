using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon.Compiler;
using RunPad.Languages.VB;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #73 — the hot-path cache-skip check (source/project file unchanged + outputDll still on
// disk) didn't distinguish "unchanged because nothing to do" from "unchanged because the last build
// for this exact source failed": MSBuild only overwrites outputDll on a SUCCESSFUL compile, so a
// failing build left the previous, no-longer-matching binary sitting in bin/ untouched. The next run
// of the SAME still-broken source then hit the hot path and silently reran the stale OLD binary
// instead of retrying the build or reporting the same error again. This is a real dotnet-build
// integration test (no mocking of the compiler) — the bug is entirely about MSBuild's actual on-disk
// behavior on a failed compile, which pure-logic unit testing can't stand in for.
//
// ANALYSIS.md #86: DotnetProcessCompiler/VbProcessCompiler were unified into one
// MsBuildProcessCompiler parameterized by DotnetLanguageProjectProfile — the two profiles below
// still each get their own real-build proof, not just an assumption that they behave the same way
// through the shared implementation.
// Category=RealBuild lets CI's fast Windows/macOS PR lane exclude this whole group via
// `dotnet test --filter "Category!=RealBuild"` — see .github/workflows/dotnet-desktop.yml.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class DotnetProcessCompilerTests
{
    private static MsBuildProcessCompiler CreateCompiler(string outputDir)
    {
        var sdk = new DotnetSdkProvider().Get().First(); // real installed SDK, same resolution as production
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();
        return new MsBuildProcessCompiler(
            CSharpProjectProfile.Instance,
            sdk: sdk,
            executionOptions: new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            outputDir: outputDir,
            runPadRuntimeLib: runtimeLib,
            outputName: "Query",
            nugetPackages: new Dictionary<string, string>(),
            usings: [],
            assemblyReferences: [],
            projectReferences: [],
            logger: NullLogger<MsBuildProcessCompiler>.Instance);
    }

    [Fact]
    public async Task Compilation_Should_NotReuseStaleSuccessfulBinary_AfterASubsequentBuildFails()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-compiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);

            var goodResult = await compiler.Compilation("System.Console.WriteLine(\"v1\");", CancellationToken.None);
            Assert.True(goodResult.Success, string.Join('\n', goodResult.Errors.Select(e => e.Message)));
            Assert.True(File.Exists(goodResult.OutputBinaryFile));

            const string brokenCode = "this is not valid C# at all {{{";
            var brokenResult = await compiler.Compilation(brokenCode, CancellationToken.None);
            Assert.False(brokenResult.Success);
            Assert.False(File.Exists(brokenResult.OutputBinaryFile)); // stale v1 binary must be gone

            // Same broken source again — before the fix, the hot path found the same source/project
            // file as last time plus a (stale) outputDll still on disk, and returned Success = true
            // using the old v1 binary instead of retrying the build.
            var secondBrokenResult = await compiler.Compilation(brokenCode, CancellationToken.None);
            Assert.False(secondBrokenResult.Success);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Regression guard for the hot-path optimization itself — the fix must only skip rebuilding
    // after an actual failure, not turn every rerun into a full rebuild.
    [Fact]
    public async Task Compilation_Should_StillUseTheHotPath_WhenTheLastBuildForThisSourceSucceeded()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-compiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            const string code = "System.Console.WriteLine(\"v1\");";

            var first = await compiler.Compilation(code, CancellationToken.None);
            Assert.True(first.Success, string.Join('\n', first.Errors.Select(e => e.Message)));
            var writeTimeAfterFirstBuild = File.GetLastWriteTimeUtc(first.OutputBinaryFile);

            var second = await compiler.Compilation(code, CancellationToken.None);

            Assert.True(second.Success);
            // Hot path returns without touching the file at all — a real rebuild would have
            // rewritten (and so re-timestamped) outputDll.
            Assert.Equal(writeTimeAfterFirstBuild, File.GetLastWriteTimeUtc(second.OutputBinaryFile));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // TODO.md #111 fix — live user report, "Util.CurrentQueryPath always null": the hot-path skip
    // above compares generated TEXT (project file/source), which references RunPad.Runtime.dll by
    // PATH only — rebuilding it in place (e.g. updating RunPad itself) left that text identical, so
    // the hot path kept serving a stale outputDll built against the OLD RunPad.Runtime.dll forever,
    // even though the path on disk now points at genuinely different content. Real dotnet-build
    // integration test (no mocking) — the bug is entirely about actual on-disk timestamps, same
    // rationale as the failed-build hot-path test above.
    [Fact]
    public async Task Compilation_Should_Rebuild_WhenTheReferencedRuntimeLibChangesOnDisk_EvenIfSourceDidnt()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-compiler-test").FullName;
        var fakeRuntimeLib = Path.Combine(
            Directory.CreateTempSubdirectory("runpad-compiler-test-lib").FullName, "RunPad.Runtime.dll");
        try
        {
            // A real, valid managed assembly (copied, not the live system one) so a real `dotnet
            // build` referencing it actually succeeds — its own content doesn't matter, only its
            // on-disk timestamp does.
            File.Copy(new CSharpReferenceService().GetExtensionReference(), fakeRuntimeLib);

            var sdk = new DotnetSdkProvider().Get().First();
            using var compiler = new MsBuildProcessCompiler(
                CSharpProjectProfile.Instance,
                sdk: sdk,
                executionOptions: new ExecutionOptions
                {
                    WorkingDirectory = outputDir,
                    Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                    Debugger = new DebuggerOptions { DebuggerPath = "" },
                },
                outputDir: outputDir,
                runPadRuntimeLib: fakeRuntimeLib,
                outputName: "Query",
                nugetPackages: new Dictionary<string, string>(),
                usings: [],
                assemblyReferences: [],
                projectReferences: [],
                logger: NullLogger<MsBuildProcessCompiler>.Instance);
            const string code = "System.Console.WriteLine(\"v1\");";

            var first = await compiler.Compilation(code, CancellationToken.None);
            Assert.True(first.Success, string.Join('\n', first.Errors.Select(e => e.Message)));
            var writeTimeAfterFirstBuild = File.GetLastWriteTimeUtc(first.OutputBinaryFile);

            // Simulate RunPad.Runtime.dll being rebuilt in place — same path, newer content/timestamp.
            File.SetLastWriteTimeUtc(fakeRuntimeLib, DateTime.UtcNow.AddMinutes(5));

            var second = await compiler.Compilation(code, CancellationToken.None);

            Assert.True(second.Success, string.Join('\n', second.Errors.Select(e => e.Message)));
            // A real rebuild rewrites (and so re-timestamps) outputDll — the hot path wouldn't have.
            Assert.NotEqual(writeTimeAfterFirstBuild, File.GetLastWriteTimeUtc(second.OutputBinaryFile));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(fakeRuntimeLib)!, recursive: true);
        }
    }

    // Bug report 2026-07-27 (live user report — "nuget packages don't restore if the .rpad says
    // `Newtonsoft.Json: latest`, and running the project just does nothing"): two separate real bugs,
    // both proven end to end here since the failure is entirely about real dotnet-build/NuGet
    // behavior, not pure logic. (1) RpadFrontMatter's Package.Default/an unpinned package uses the
    // literal string "latest" — meaningful app-side vocabulary, but not valid NuGet version syntax
    // (confirmed directly: `dotnet build` with Version="latest" failed with "'latest' is not a valid
    // version string"); MsBuildProcessCompiler now translates it to NuGet's real "any latest version"
    // wildcard, "*". Newtonsoft.Json (a real, always-available package) proves the whole chain — not
    // just that "*" appears in the generated XML, but that NuGet actually accepts it and restores.
    [Fact]
    public async Task Compilation_Should_ResolveALatestPinnedPackage_ToARealSuccessfulRestore()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-compiler-test").FullName;
        try
        {
            var sdk = new DotnetSdkProvider().Get().First();
            var runtimeLib = new CSharpReferenceService().GetExtensionReference();
            using var compiler = new MsBuildProcessCompiler(
                CSharpProjectProfile.Instance,
                sdk: sdk,
                executionOptions: new ExecutionOptions
                {
                    WorkingDirectory = outputDir,
                    Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                    Debugger = new DebuggerOptions { DebuggerPath = "" },
                },
                outputDir: outputDir,
                runPadRuntimeLib: runtimeLib,
                outputName: "Query",
                nugetPackages: new Dictionary<string, string> { ["Newtonsoft.Json"] = "latest" },
                usings: [],
                assemblyReferences: [],
                projectReferences: [],
                logger: NullLogger<MsBuildProcessCompiler>.Instance);

            var result = await compiler.Compilation(
                "System.Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(new { a = 1 }));",
                CancellationToken.None);

            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));
            var projectFileText = await File.ReadAllTextAsync(
                Path.Combine(outputDir, "Query.csproj"), CancellationToken.None);
            Assert.Contains("""Version="*" """.Trim(), projectFileText);
            Assert.DoesNotContain("""Version="latest" """.Trim(), projectFileText);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // (2) The error/warning regexes required a NON-empty diagnostic code (\S+) — but the real
    // "not a valid version string" restore failure has NO code at all ("...NuGet.targets(196,5):
    // error : 'latest' is not a valid version string.", confirmed by actually forcing this exact
    // failure). That line matched neither regex, so a failed build (ExitCode != 0, Success correctly
    // false) came back with an EMPTY Errors list — RunAsync's `if (!compilationResult.Success ...)
    // return;` then had nothing to show, so nothing appeared anywhere and nothing ran. A different,
    // still-invalid, non-"latest" version string (so fix #1's translation above doesn't mask this one)
    // reproduces the same empty-code failure shape independently of that fix.
    [Fact]
    public async Task Compilation_Should_ReportTheError_ForARestoreFailureWithNoDiagnosticCode()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-compiler-test").FullName;
        try
        {
            var sdk = new DotnetSdkProvider().Get().First();
            var runtimeLib = new CSharpReferenceService().GetExtensionReference();
            using var compiler = new MsBuildProcessCompiler(
                CSharpProjectProfile.Instance,
                sdk: sdk,
                executionOptions: new ExecutionOptions
                {
                    WorkingDirectory = outputDir,
                    Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                    Debugger = new DebuggerOptions { DebuggerPath = "" },
                },
                outputDir: outputDir,
                runPadRuntimeLib: runtimeLib,
                outputName: "Query",
                nugetPackages: new Dictionary<string, string> { ["Newtonsoft.Json"] = "not-a-real-version" },
                usings: [],
                assemblyReferences: [],
                projectReferences: [],
                logger: NullLogger<MsBuildProcessCompiler>.Instance);

            var result = await compiler.Compilation("System.Console.WriteLine(1);", CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors); // was empty before the fix, despite Success already being false
            Assert.Contains(result.Errors, e => e.Message.Contains("not a valid version string"));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    private static MsBuildProcessCompiler CreateVbCompiler(string outputDir)
    {
        var sdk = new DotnetSdkProvider().Get().First();
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();
        return new MsBuildProcessCompiler(
            VbProjectProfile.Instance,
            sdk: sdk,
            executionOptions: new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            outputDir: outputDir,
            runPadRuntimeLib: runtimeLib,
            outputName: "Query",
            nugetPackages: new Dictionary<string, string>(),
            usings: [],
            assemblyReferences: [],
            projectReferences: [],
            logger: NullLogger<MsBuildProcessCompiler>.Instance);
    }

    [Fact]
    public async Task VbCompilation_Should_NotReuseStaleSuccessfulBinary_AfterASubsequentBuildFails()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-vb-compiler-test").FullName;
        try
        {
            using var compiler = CreateVbCompiler(outputDir);

            const string goodCode = "Module Program\nSub Main()\nConsole.WriteLine(\"v1\")\nEnd Sub\nEnd Module\n";
            var goodResult = await compiler.Compilation(goodCode, CancellationToken.None);
            Assert.True(goodResult.Success, string.Join('\n', goodResult.Errors.Select(e => e.Message)));
            Assert.True(File.Exists(goodResult.OutputBinaryFile));

            const string brokenCode = "this is not valid VB at all {{{";
            var brokenResult = await compiler.Compilation(brokenCode, CancellationToken.None);
            Assert.False(brokenResult.Success);
            Assert.False(File.Exists(brokenResult.OutputBinaryFile));

            var secondBrokenResult = await compiler.Compilation(brokenCode, CancellationToken.None);
            Assert.False(secondBrokenResult.Success);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // Live user request 2026-07-28: "make our runtime fully compatible with the LINQPad Runtime
    // API". Part of that is DumpContainer/ICustomMemberProvider/DumpOptions being usable by short
    // name with no explicit `using RunPad.Runtime;`, matching how LINQPad's own query host implicitly
    // imports the whole LINQPad namespace for every query — Util/.Dump() already worked this way
    // (declared in the GLOBAL namespace specifically so extension-method/static lookup never needed
    // an import at all); this proves it now also holds for the ordinary, non-extension types, via
    // CSharpProjectProfile.DefaultImportNamespaces + the matching <Using Include="RunPad.Runtime" />
    // project item (MsBuildProcessCompiler.BuildProjectFile). Real dotnet-build integration test —
    // whether an unqualified type name resolves is entirely about what the generated project file
    // actually contains, not something unit-testable without a real compile.
    [Fact]
    public async Task Compilation_Should_ResolveDumpContainerAndICustomMemberProvider_WithNoExplicitUsingStatement()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-compiler-test").FullName;
        try
        {
            using var compiler = CreateCompiler(outputDir);
            const string code =
                "var dc = new DumpContainer(\"hello\");\n" +
                "dc.Dump();\n" +
                "System.Console.WriteLine(dc.Content);\n" +
                "class Provider : ICustomMemberProvider\n" +
                "{\n" +
                "    public System.Collections.Generic.IEnumerable<string> GetNames() => new[] { \"A\" };\n" +
                "    public System.Collections.Generic.IEnumerable<System.Type> GetTypes() => new[] { typeof(int) };\n" +
                "    public System.Collections.Generic.IEnumerable<object?> GetValues() => new object?[] { 1 };\n" +
                "}\n";

            var result = await compiler.Compilation(code, CancellationToken.None);

            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    // VB counterpart — proves VbProjectProfile.DefaultImportNamespaces (already the real "no
    // <ImplicitUsings>, use <Import> items instead" mechanism VB has always used for its own BCL
    // default-import list) correctly carries RunPad.Runtime alongside those too.
    [Fact]
    public async Task VbCompilation_Should_ResolveDumpContainer_WithNoExplicitImportsStatement()
    {
        var outputDir = Directory.CreateTempSubdirectory("runpad-vb-compiler-test").FullName;
        try
        {
            using var compiler = CreateVbCompiler(outputDir);
            const string code =
                "Module Program\n" +
                "Sub Main()\n" +
                "Dim dc = New DumpContainer(\"hello\")\n" +
                "dc.Dump()\n" +
                "Console.WriteLine(dc.Content)\n" +
                "End Sub\n" +
                "End Module\n";

            var result = await compiler.Compilation(code, CancellationToken.None);

            Assert.True(result.Success, string.Join('\n', result.Errors.Select(e => e.Message)));
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
