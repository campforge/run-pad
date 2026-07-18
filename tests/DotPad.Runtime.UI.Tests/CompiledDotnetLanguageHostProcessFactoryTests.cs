using DotPad.Build.Core.Models;
using DotPad.Build.Core.ProcessRunners;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.DotnetCommon;
using DotPad.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #88 (остаток): RunAsync/RunDebugAsync used to always ChildProcess.ForExecution(...)/
// ForDebugger(...) directly, so exercising them meant either shelling out to a real dotnet/netcoredbg
// process or not testing them at all — the same gap CompiledDotnetLanguageHostCompilerFactoryTests
// already closed for the compiler seam. The injected ExecutionChildProcessFactory/
// DebuggerChildProcessFactory let a fake process stand in instead.
public class CompiledDotnetLanguageHostProcessFactoryTests
{
    private sealed class FakeCompiler(ProcessCompilerResult result) : IDotnetProcessCompiler
    {
        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) => Task.FromResult(result);

        public void Dispose()
        {
        }
    }

    // Empty in-memory streams are enough here: DrainReaderAsync's ReadLineAsync (Run) and
    // DapSession's ReadMessageAsync (RunDebug) both treat an immediate EOF as "nothing more to read"
    // and return cleanly — no real dotnet/netcoredbg process, no protocol scripting needed.
    private sealed class FakeChildProcess : IChildProcess
    {
        public bool Started { get; private set; }

        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();

        public bool Start()
        {
            Started = true;
            return true;
        }

        public void Kill()
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (CSharpLanguageHost Host, string OutputDir) CreateHost(
        ExecutionChildProcessFactory? executionProcessFactory = null,
        DebuggerChildProcessFactory? debuggerProcessFactory = null)
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
            options, NullLoggerFactory.Instance, (_, _, _) => compiler, executionProcessFactory, debuggerProcessFactory);
        return (host, outputDir);
    }

    [Fact]
    public async Task RunAsync_Should_UseTheInjectedExecutionProcess_InsteadOfShellingOutToDotnet()
    {
        FakeChildProcess? received = null;
        string? receivedAssemblyPath = null;
        ExecutionChildProcessFactory factory = (options, assemblyPath, _) =>
        {
            receivedAssemblyPath = assemblyPath;
            return received = new FakeChildProcess();
        };

        var (host, outputDir) = CreateHost(executionProcessFactory: factory);
        try
        {
            await host.RunAsync("int x = 1;", cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(received);
            Assert.True(received.Started);
            Assert.Equal("out.dll", receivedAssemblyPath);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }

    [Fact]
    public async Task RunDebugAsync_Should_UseTheInjectedDebuggerProcess_InsteadOfShellingOutToNetcoredbg()
    {
        FakeChildProcess? received = null;
        DebuggerChildProcessFactory factory = _ => received = new FakeChildProcess();

        var (host, outputDir) = CreateHost(debuggerProcessFactory: factory);
        try
        {
            await host.RunDebugAsync(
                "int x = 1;", outputHandler: static (_, _) => { }, TestContext.Current.CancellationToken);

            Assert.NotNull(received);
            Assert.True(received.Started);
        }
        finally { Directory.Delete(outputDir, recursive: true); }
    }
}
