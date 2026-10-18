using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Build.Core.ProcessRunners;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using RunPad.Languages.DotnetCommon.Sdk;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// ILanguageHost.InputRequested/SendInputAsync, the
// host-side half of RunPad.Runtime.Util.ReadLine/GetPassword (see UtilTests.cs for the runtime-side
// half). '\x1f' below mirrors RunPad.Runtime.Util.InputRequestPrefixChar/
// CompiledDotnetLanguageHostBase.InputRequestPrefix directly (internal to their own assemblies, not
// visible here — RunPad.Runtime only grants InternalsVisibleTo to RunPad.Runtime.Tests).
//
// Test-design cleanup (real `dotnet build`/child-process round trips test the
// compiler and the OS, not RunPad's own code) removed this file's one real end-to-end test
// (RunAsync_Should_SupportARealScriptCallingUtilReadLine_EndToEnd): every piece it proved is already
// covered here in isolation — InputRequested_Should_FireWithThePromptAndIsPasswordFlag/
// SendInputAsync_Should_WriteTheTextPlusNewline_ToTheProcessStdin below exercise the host's own
// sentinel-detection and stdin-write logic via FakeChildProcess, and
// RunPad.Runtime.Tests.UtilTests.ReadLine_WritesSentinelPrefixedRequest_ThenReturnsTheStdinAnswer
// proves Util.ReadLine's writer produces the exact same wire format these fakes assume. The only
// thing the real version additionally touched — whether a live OS pipe actually delivers what's
// written to Process.StandardInput — is .NET's own Process/pipe behavior, not RunPad's.
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
        public int ExitCode => 0;

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
        var outputDir = Path.Combine(Path.GetTempPath(), "runpad-tests-" + Guid.NewGuid());
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
            Kind = RpadKind.Statements,
        };

        var compiler = new FakeCompiler(new ProcessCompilerResult("out.dll", true, [], []));
        var host = new CSharpLanguageHost(
            options, NullLoggerFactory.Instance, (_, _, _) => compiler,
            executionProcessFactory: (_, _, _, _, _) => process);
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
