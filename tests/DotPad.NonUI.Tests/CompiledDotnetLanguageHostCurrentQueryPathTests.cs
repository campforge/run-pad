using DotPad.Build.Core.Models;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #111 (bucket 1) — LINQPad-compatible Util.CurrentQueryPath. Real dotnet-build + real
// spawned process integration test (no mocking of the compiler or the child process), same shape as
// CompiledDotnetLanguageHostInputTests' own Util.ReadLine end-to-end test — proves the WHOLE round
// trip: CompiledDotnetLanguageHostOptions.CurrentQueryPath really does reach ChildProcess.ForExecution
// as an environment variable, and DotPad.Runtime.Util.CurrentQueryPath running inside the real
// compiled script's own process really does read it back correctly.
public class CompiledDotnetLanguageHostCurrentQueryPathTests
{
    // Windows CI report: Directory.Delete(outputDir) right after RunAsync returns threw
    // UnauthorizedAccessException on "DotPad.Runtime.dll" — the compiled script's own process has
    // already exited by then (RunAsync's ChildProcess is disposed, via its own `await using`, before
    // RunAsync returns), but on Windows specifically the OS can hold a loaded module's file lock open
    // for a brief moment past that (deferred handle release, sometimes AV-scanner interference) —
    // deleting a directory containing a file another process still has open works fine on Linux/
    // macOS's POSIX unlink semantics, which is why this never showed up there. Retries instead of
    // assuming the delete always succeeds on the first try.
    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                await Task.Delay(200);
            }
        }

        Directory.Delete(path, recursive: true); // last attempt — let a real, persistent failure throw
    }

    private static CompiledDotnetLanguageHostOptions BuildOptions(string outputDir, string? currentQueryPath)
    {
        var sdk = new DotnetSdkProvider().Get().First();
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();
        return new CompiledDotnetLanguageHostOptions
        {
            Platform = sdk,
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = outputDir,
            RuntimeLibPath = runtimeLib,
            Kind = RpadKind.Statements,
            CurrentQueryPath = currentQueryPath,
        };
    }

    [Fact]
    public async Task RunAsync_Should_ExposeCurrentQueryPath_ToARealScript_EndToEnd()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "dotpad-tests-" + Guid.NewGuid());
        var queryPath = Path.Combine(Path.GetTempPath(), "MyScript.rpad");
        await using var host = new CSharpLanguageHost(
            BuildOptions(outputDir, currentQueryPath: queryPath), NullLoggerFactory.Instance);
        try
        {
            var output = new List<string>();
            const string code = """System.Console.WriteLine("QueryPath=" + (Util.CurrentQueryPath ?? "<null>"));""";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            await host.RunAsync(code, (data, _) => output.Add(data), timeoutCts.Token);

            Assert.Contains($"QueryPath={queryPath}", output);
        }
        finally { await DeleteDirectoryWithRetryAsync(outputDir); }
    }

    [Fact]
    public async Task RunAsync_Should_ExposeCurrentQueryPathAsNull_ForAnUntitledTab_EndToEnd()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "dotpad-tests-" + Guid.NewGuid());
        await using var host = new CSharpLanguageHost(
            BuildOptions(outputDir, currentQueryPath: null), NullLoggerFactory.Instance);
        try
        {
            var output = new List<string>();
            const string code = """System.Console.WriteLine("QueryPath=" + (Util.CurrentQueryPath ?? "<null>"));""";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            await host.RunAsync(code, (data, _) => output.Add(data), timeoutCts.Token);

            Assert.Contains("QueryPath=<null>", output);
        }
        finally { await DeleteDirectoryWithRetryAsync(outputDir); }
    }
}
