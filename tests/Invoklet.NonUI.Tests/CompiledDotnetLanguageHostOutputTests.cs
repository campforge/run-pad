using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Build.Core.Models;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Xunit;

namespace Invoklet.NonUI.Tests;

// How the host turns a child's raw stdout/stderr into OutputReceivedHandler calls: complete lines,
// text without a line terminator (Console.Write), Dump records that follow such text, and compiler
// diagnostics kept apart from the script's output. The sentinel characters mirror
// Invoklet.Runtime.ObjectExtensions.DumpLinePrefixChar ('\x1e') and Util.InputRequestPrefixChar ('\x1f').
public class CompiledDotnetLanguageHostOutputTests
{
    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) => Task.FromResult(result);

        public void Dispose()
        {
        }
    }

    private sealed class FakeChildProcess(string stdout, string stderr = "") : IChildProcess
    {
        public StreamReader StandardOutput { get; } = Reader(stdout);
        public StreamReader StandardError { get; } = Reader(stderr);
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();

        private static StreamReader Reader(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

        public bool Start() => true;

        public void Kill()
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public int ExitCode => 0;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Record(string html) => '\x1e' + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));

    private static async Task<List<(string Data, OutputKind Kind)>> RunAsync(
        string stdout, string stderr = "", ProcessCompilerResult? compiled = null)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "Invoklet-tests-" + Guid.NewGuid());
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
            RuntimeLibPath = "Invoklet.Runtime.dll",
            Kind = RpadKind.Statements,
        };
        var compiler = new FakeCompiler(compiled ?? new ProcessCompilerResult("out.dll", true, [], []));
        await using var host = new CSharpLanguageHost(
            options, NullLoggerFactory.Instance, (_, _, _) => compiler,
            executionProcessFactory: (_, _, _, _, _) => new FakeChildProcess(stdout, stderr));

        var received = new List<(string, OutputKind)>();
        await host.RunAsync("", (data, kind) => received.Add((data, kind)), CancellationToken.None);
        return received;
    }

    [Fact]
    public async Task RunAsync_Should_NotTerminateTextWrittenWithoutALineBreak()
    {
        var received = await RunAsync("abc");

        Assert.Equal([("abc", OutputKind.StandardOutputPartial)], received);
    }

    [Fact]
    public async Task RunAsync_Should_KeepConsoleWriteAndTheFollowingLineApart()
    {
        // Console.Write("a"); Console.Write("b"); Console.WriteLine("c"); Console.Write("d");
        var received = await RunAsync("abc\nd");

        Assert.Equal([("abc", OutputKind.StandardOutput), ("d", OutputKind.StandardOutputPartial)], received);
    }

    [Fact]
    public async Task RunAsync_Should_KeepBlankLinesAndStripCarriageReturns()
    {
        var received = await RunAsync("a\r\n\r\nb\n");

        Assert.Equal(
            [("a", OutputKind.StandardOutput), ("", OutputKind.StandardOutput), ("b", OutputKind.StandardOutput)], received);
    }

    [Fact]
    public async Task RunAsync_Should_FindADumpRecordAfterTextWithoutALineBreak()
    {
        // Console.Write("total: "); 42.Dump();
        var received = await RunAsync("total: " + Record("<b>42</b>") + "\n");

        Assert.Equal(
            [("total: ", OutputKind.StandardOutputPartial), (Record("<b>42</b>")[1..], OutputKind.DumpHtml)], received);
    }

    [Fact]
    public async Task RunAsync_Should_FindAdjacentDumpRecords()
    {
        var received = await RunAsync("x" + Record("1") + "\n" + Record("2") + "\ny\n");

        Assert.Equal(
            [
                ("x", OutputKind.StandardOutputPartial),
                (Record("1")[1..], OutputKind.DumpHtml),
                (Record("2")[1..], OutputKind.DumpHtml),
                ("y", OutputKind.StandardOutput),
            ], received);
    }

    [Fact]
    public async Task RunAsync_Should_LeaveTextThatOnlyContainsTheSentinelCharacterAlone()
    {
        // Not a valid payload after the sentinel: the script wrote it, so it is the script's output.
        var received = await RunAsync("a\x1e not base64!\n\x1e\n");

        Assert.Equal(
            [("a\x1e not base64!", OutputKind.StandardOutput), ("\x1e", OutputKind.StandardOutput)], received);
    }

    [Fact]
    public async Task RunAsync_Should_KeepStandardErrorApartFromStandardOutput()
    {
        // A sentinel on stderr is just text: only the script's stdout carries records.
        var received = await RunAsync("out\n", "err\n" + Record("x") + "\ntail");

        Assert.Equal(
            ["out"], received.Where(r => r.Kind == OutputKind.StandardOutput).Select(r => r.Data));
        Assert.Equal(
            [("err", OutputKind.StandardError), (Record("x"), OutputKind.StandardError), ("tail", OutputKind.StandardErrorPartial)],
            received.Where(r => r.Kind is OutputKind.StandardError or OutputKind.StandardErrorPartial));
        Assert.DoesNotContain(received, r => r.Kind == OutputKind.DumpHtml);
    }

    [Fact]
    public async Task RunAsync_Should_DeliverPendingTextBeforeLaterOutputOfTheOtherStream()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "Invoklet-tests-" + Guid.NewGuid());
        var stdout = new AnnouncingPipe();
        var stderr = new AnnouncingPipe();
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
            RuntimeLibPath = "Invoklet.Runtime.dll",
            Kind = RpadKind.Statements,
        };
        var compiler = new FakeCompiler(new ProcessCompilerResult("out.dll", true, [], []));
        await using var host = new CSharpLanguageHost(
            options, NullLoggerFactory.Instance, (_, _, _) => compiler,
            executionProcessFactory: (_, _, _, _, _) => new PipedChildProcess(stdout, stderr));

        var received = new List<(string, OutputKind)>();
        var run = host.RunAsync("", (data, kind) => received.Add((data, kind)), CancellationToken.None);

        // Console.Write("a") on stdout, then a line on stderr, then the rest of stdout's line.
        await stdout.WriteAsync("a");
        await stderr.WriteAsync("oops\n");
        await stdout.WriteAsync("b\n");
        stdout.Complete();
        stderr.Complete();
        await run;

        Assert.Equal(
            [("a", OutputKind.StandardOutputPartial), ("oops", OutputKind.StandardError), ("b", OutputKind.StandardOutput)],
            received);
    }

    [Fact]
    public async Task RunAsync_Should_NotSendCompilerWarningsAsScriptOutput()
    {
        var warning = new ResultItem("CS0219", "a.cs", new LineSymbol(3, 5), "The variable 'x' is assigned but never used");
        var compiled = new ProcessCompilerResult("out.dll", true, [], [warning]);

        var received = await RunAsync("hello\n", compiled: compiled);

        Assert.Contains(received, r => r.Kind == OutputKind.Diagnostic && r.Data.Contains("CS0219"));
        Assert.DoesNotContain(received, r => r.Kind == OutputKind.StandardOutput && r.Data.Contains("CS0219"));
        Assert.Contains(("hello", OutputKind.StandardOutput), received);
    }

    // A pipe the test feeds by hand and that delivers each write as its own read.
    private sealed class AnnouncingPipe
    {
        private readonly System.IO.Pipelines.Pipe _pipe = new();

        public Stream Stream => _pipe.Reader.AsStream();

        public async Task WriteAsync(string text)
        {
            await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
            await Task.Delay(100); // let the drain read it before the next write
        }

        public void Complete() => _pipe.Writer.Complete();
    }

    private sealed class PipedChildProcess(AnnouncingPipe stdout, AnnouncingPipe stderr) : IChildProcess
    {
        public StreamReader StandardOutput { get; } = new(stdout.Stream);
        public StreamReader StandardError { get; } = new(stderr.Stream);
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();

        public bool Start() => true;

        public void Kill()
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public int ExitCode => 0;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
