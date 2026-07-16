using DotPad.Build.Core.Models;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon.Compiler;
using DotPad.Languages.VB;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

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
            dotPadRuntimeLib: runtimeLib,
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
        var outputDir = Directory.CreateTempSubdirectory("dotpad-compiler-test").FullName;
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
        var outputDir = Directory.CreateTempSubdirectory("dotpad-compiler-test").FullName;
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
            dotPadRuntimeLib: runtimeLib,
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
        var outputDir = Directory.CreateTempSubdirectory("dotpad-vb-compiler-test").FullName;
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
}
