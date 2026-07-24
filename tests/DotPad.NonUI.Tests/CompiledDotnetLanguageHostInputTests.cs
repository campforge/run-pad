using System.Text;
using DotPad.Build.Core.Models;
using DotPad.Build.Core.ProcessRunners;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #63 "интерактивный типизированный ввод" — ILanguageHost.InputRequested/SendInputAsync, the
// host-side half of DotPad.Runtime.Util.ReadLine/GetPassword (see UtilTests.cs for the runtime-side
// half). '\x1f' below mirrors DotPad.Runtime.Util.InputRequestPrefixChar/
// CompiledDotnetLanguageHostBase.InputRequestPrefix directly (internal to their own assemblies, not
// visible here — DotPad.Runtime only grants InternalsVisibleTo to DotPad.Runtime.Tests).
//
// CI-cost pass: only RunAsync_Should_SupportARealScriptCallingUtilReadLine_EndToEnd (below, split into
// CompiledDotnetLanguageHostRealInputTests) spawns an actual `dotnet build`/child process — everything
// in THIS class drives CSharpLanguageHost through FakeCompiler/FakeChildProcess and never touches
// MSBuild, so it doesn't need (and shouldn't pay for) RealBuildTestCollection's forced serialization.
public class CompiledDotnetLanguageHostInputTests
{
    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) => Task.FromResult(result);

        public void Dispose()
        {
        }
    }

    // Never reaches EOF on its own, same rationale/shape as
    // CompiledDotnetLanguageHostTerminateAsyncTests.BlockingStream — keeps RunAsync genuinely
    // in-flight so a test can call SendInputAsync while _currentInputStream is actually set.
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeChildProcess : IChildProcess
    {
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream { get; } = new MemoryStream();
        public MemoryStream InputStream { get; } = new();
        Stream IChildProcess.InputStream => InputStream;

        public FakeChildProcess(Stream? stdout = null) => StandardOutput = new StreamReader(stdout ?? new BlockingStream());

        public bool Start() => true;

        public void Kill()
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string BuildSentinelLine(string? prompt, bool isPassword)
    {
        var json = $$"""{"Prompt":{{(prompt is null ? "null" : $"\"{prompt}\"")}},"IsPassword":{{(isPassword ? "true" : "false")}}}""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return '\x1f' + encoded;
    }

    private static (CSharpLanguageHost Host, string OutputDir) CreateHost(FakeChildProcess process)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "dotpad-tests-" + Guid.NewGuid());
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
            RuntimeLibPath = "DotPad.Runtime.dll",
            Kind = RpadKind.Statements,
        };

        var compiler = new FakeCompiler(new ProcessCompilerResult("out.dll", true, [], []));
        var host = new CSharpLanguageHost(
            options, NullLoggerFactory.Instance, (_, _, _) => compiler,
            executionProcessFactory: (_, _, _, _) => process);
        return (host, outputDir);
    }

    [Fact]
    public async Task InputRequested_Should_FireWithThePromptAndIsPasswordFlag()
    {
        var stdout = new MemoryStream(Encoding.UTF8.GetBytes(BuildSentinelLine("Name?", isPassword: false) + "\n"));
        var (host, outputDir) = CreateHost(new FakeChildProcess(stdout));
        try
        {
            string? receivedPrompt = "not set";
            var receivedIsPassword = true;
            host.InputRequested += (prompt, isPassword) =>
            {
                receivedPrompt = prompt;
                receivedIsPassword = isPassword;
            };

            // Sentinel detection (Dump AND InputRequested alike) only runs when RunAsync is given a
            // real outputHandler — see WrapWithSentinelDetection's own null-handler early return.
            await host.RunAsync("int x = 1;", (_, _) => { }, TestContext.Current.CancellationToken);

            Assert.Equal("Name?", receivedPrompt);
            Assert.False(receivedIsPassword);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task InputRequested_Should_ReportIsPasswordTrue_ForAGetPasswordRequest()
    {
        var stdout = new MemoryStream(Encoding.UTF8.GetBytes(BuildSentinelLine("Password?", isPassword: true) + "\n"));
        var (host, outputDir) = CreateHost(new FakeChildProcess(stdout));
        try
        {
            var receivedIsPassword = false;
            host.InputRequested += (_, isPassword) => receivedIsPassword = isPassword;

            await host.RunAsync("int x = 1;", (_, _) => { }, TestContext.Current.CancellationToken);

            Assert.True(receivedIsPassword);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task SendInputAsync_Should_WriteTheTextPlusNewline_ToTheProcessStdin()
    {
        var process = new FakeChildProcess();
        var (host, outputDir) = CreateHost(process);
        try
        {
            var runTask = host.RunAsync("int x = 1;", cancellationToken: TestContext.Current.CancellationToken);

            // Yield once so RunAsync can start and set up its streams before we send input.

            await Task.Yield();


            await host.SendInputAsync("Alice", TestContext.Current.CancellationToken);

            Assert.Equal("Alice\n", Encoding.UTF8.GetString(process.InputStream.ToArray()));
        }
        finally
        {
            await host.TerminateAsync(TestContext.Current.CancellationToken);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task SendInputAsync_Should_NotThrow_WhenNothingIsCurrentlyRunning()
    {
        var (host, outputDir) = CreateHost(new FakeChildProcess());
        try
        {
            await host.SendInputAsync("Alice", TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

}

// Split from CompiledDotnetLanguageHostInputTests (CI-cost pass) — this is the one test in that group
// that spawns a real `dotnet build` + real child process, so only it needs RealBuildTestCollection's
// forced serialization; the fake-backed tests next to it don't and now run fully parallel instead.
// Category=RealBuild lets CI's fast Windows/macOS PR lane exclude this whole group via
// `dotnet test --filter "Category!=RealBuild"` — see .github/workflows/dotnet-desktop.yml.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class CompiledDotnetLanguageHostRealInputTests
{
    // Real dotnet-build + real spawned process integration test (no mocking of the compiler or the
    // child process) — proves the WHOLE round trip works: DotPad.Runtime.Util.ReadLine really does
    // write the sentinel line a real compiled script's stdout, CompiledDotnetLanguageHostBase really
    // does detect it and raise InputRequested, and SendInputAsync's reply really does unblock the
    // real process's Console.In.ReadLine() and flow back out through stdout.
    [Fact]
    public async Task RunAsync_Should_SupportARealScriptCallingUtilReadLine_EndToEnd()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "dotpad-tests-" + Guid.NewGuid());
        var sdk = new DotnetSdkProvider().Get().First();
        var runtimeLib = new CSharpReferenceService().GetExtensionReference();
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
            RuntimeLibPath = runtimeLib,
            Kind = RpadKind.Statements,
        };
        await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        try
        {
            string? receivedPrompt = null;
            host.InputRequested += async (prompt, _) =>
            {
                receivedPrompt = prompt;
                await host.SendInputAsync("World", CancellationToken.None);
            };

            var output = new List<string>();
            const string code = """
                var name = Util.ReadLine("Name?");
                System.Console.WriteLine("Hello, " + name);
                """;

            // A regression that breaks sentinel detection leaves the real child process blocked
            // forever on Console.In.ReadLine() instead of failing an assertion — bound it so that
            // shows up as a clean timeout instead of hanging the whole test run.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            await host.RunAsync(code, (data, _) => output.Add(data), timeoutCts.Token);

            Assert.Equal("Name?", receivedPrompt);
            Assert.Contains("Hello, World", output);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
