using System.Collections.Concurrent;
using System.Diagnostics;
using Invoklet.Build.Core.Models;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Runtime.UI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests;

// Real runs (a real SDK and dotnet process): Program scripts' Main overloads, the exit code of an
// int Main, and Stop asking a script that listens for it (Util.CancellationToken) to finish first.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class ScriptMainAndStopRunTests
{
    private sealed record Run(RunResult? Result, string Output, bool Cancelled, TimeSpan StopToEnd);

    private static CSharpLanguageHost CreateHost(string outputDir, RpadKind kind)
    {
        var sdk = new DotnetSdkProvider().Get().First();
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
            Kind = kind,
            Usings = [],
        };
        return new CSharpLanguageHost(options, NullLoggerFactory.Instance);
    }

    // Runs the script; with stopAfterLine, presses Stop once the script prints that line.
    private static async Task<Run> RunAsync(RpadKind kind, string code, string? stopAfterLine = null)
    {
        var outputDir = Directory.CreateTempSubdirectory("Invoklet-main-test").FullName;
        try
        {
            await using var host = CreateHost(outputDir, kind);
            var lines = new ConcurrentQueue<string>();
            using var stop = new CancellationTokenSource();
            var sinceStop = new Stopwatch();
            OutputReceivedHandler output = (data, _) =>
            {
                lines.Enqueue(data);
                if (data != stopAfterLine) return;
                sinceStop.Start();
                stop.Cancel();
            };
            try
            {
                var result = await host.RunAsync(code, output, stop.Token)
                    .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
                return new Run(result, string.Join('\n', lines), false, sinceStop.Elapsed);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return new Run(null, string.Join('\n', lines), true, sinceStop.Elapsed);
            }
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task MainWithArgs_Should_GetEmptyArgs()
    {
        var run = await RunAsync(RpadKind.Program, """
            void Main(string[] args)
            {
                Console.WriteLine($"args: {args.Length}");
            }
            """);

        Assert.True(run.Result?.Succeeded, run.Output);
        Assert.Contains("args: 0", run.Output);
    }

    // Invoklet's own internal arguments used to reach every script's args ("--pid 1234").
    [Fact]
    public async Task AStatementsScript_Should_GetEmptyArgs()
    {
        var run = await RunAsync(RpadKind.Statements, """Console.WriteLine($"args: {args.Length}");""");

        Assert.True(run.Result?.Succeeded, run.Output);
        Assert.Contains("args: 0", run.Output);
    }

    [Theory]
    [InlineData("int Main() { return 3; }")]
    [InlineData("async Task<int> Main(string[] args) { await Task.Yield(); return 3; }")]
    public async Task AnIntMain_Should_SetTheExitCode(string code)
    {
        var run = await RunAsync(RpadKind.Program, code);

        Assert.Equal(RunResult.Exited(3), run.Result);
    }

    // Of several Main overloads, the one taking arguments and a token is called.
    [Fact]
    public async Task OfSeveralMains_TheOneWithArgsAndToken_Should_BeCalled()
    {
        var run = await RunAsync(RpadKind.Program, """
            void Main() => Console.WriteLine("plain");

            void Main(string[] args, CancellationToken ct) => Console.WriteLine($"args and token: {ct.CanBeCanceled}");
            """);

        Assert.True(run.Result?.Succeeded, run.Output);
        Assert.Contains("args and token: True", run.Output);
        Assert.DoesNotContain("plain", run.Output);
    }

    [Fact]
    public async Task Stop_Should_LetAMainWithATokenCleanUp()
    {
        var run = await RunAsync(RpadKind.Program, """
            async Task Main(string[] args, CancellationToken ct)
            {
                Console.WriteLine("started");
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("cleaned up");
                }
            }
            """, stopAfterLine: "started");

        Assert.True(run.Cancelled, run.Output);
        Assert.Contains("cleaned up", run.Output);
    }

    [Fact]
    public async Task Stop_Should_LetAStatementsScriptUsingUtilCancellationTokenCleanUp()
    {
        var run = await RunAsync(RpadKind.Statements, """
            using var registration = Util.CancellationToken.Register(() => Console.WriteLine("cleaned up"));
            Console.WriteLine("started");
            Thread.Sleep(Timeout.Infinite);
            """, stopAfterLine: "started");

        Assert.True(run.Cancelled, run.Output);
        Assert.Contains("cleaned up", run.Output);
    }

    // A script that never asks for the token is ended at once, without the grace period.
    [Fact]
    public async Task Stop_Should_EndAScriptWithoutATokenAtOnce()
    {
        var run = await RunAsync(RpadKind.Statements, """
            Console.WriteLine("started");
            Thread.Sleep(Timeout.Infinite);
            """, stopAfterLine: "started");

        Assert.True(run.Cancelled, run.Output);
        Assert.True(run.StopToEnd < CompiledDotnetLanguageHostBase.StopGracePeriod, $"took {run.StopToEnd} after Stop");
    }
}
