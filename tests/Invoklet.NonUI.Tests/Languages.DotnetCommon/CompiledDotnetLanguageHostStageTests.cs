using Invoklet.Build.Core.Models;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The stages a compile or run goes through reach the host's StageChanged in order: what the
// compiler reports, then Running once the script's process has started. A failed or cancelled stage
// is the last one reported.
[Trait("Category", "Integration")]
[Trait("Category", "RealBuild")]
public sealed class CompiledDotnetLanguageHostStageTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), "Invoklet-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true);
    }

    private sealed class StagedCompiler(
        ProcessCompilerResult result, params (RunStage Stage, string? Detail)[] stages) : IDotnetProcessCompiler
    {
        public event StageChangedHandler? StageChanged;
        public int Subscribers => StageChanged?.GetInvocationList().Length ?? 0;

        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx)
        {
            foreach (var (stage, detail) in stages)
            {
                ctx.ThrowIfCancellationRequested();
                StageChanged?.Invoke(stage, detail);
            }
            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ExitedProcess : IChildProcess
    {
        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
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

    private CSharpLanguageHost CreateHost(StagedCompiler compiler)
    {
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk("net10.0", "net10.0", "10.0.0", "net10.0"),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = _outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "netcoredbg" },
            },
            OutputDirectory = _outputDir,
            RuntimeLibPath = "Invoklet.Runtime.dll",
            Kind = MdRunKind.Statements,
        };
        return new CSharpLanguageHost(options, NullLoggerFactory.Instance, (_, _, _) => compiler,
            executionProcessFactory: (_, _, _, _, _) => new ExitedProcess());
    }

    private static readonly (RunStage, string?)[] AllStages =
        [(RunStage.RestoringPackages, "2 packages"), (RunStage.GeneratingConnections, "1 connection source"), (RunStage.Compiling, null)];

    [Fact]
    public async Task RunAsync_Should_ReportTheCompilersStages_ThenRunning_InOrder()
    {
        var compiler = new StagedCompiler(new ProcessCompilerResult("out.dll", true, [], []), AllStages);
        var host = CreateHost(compiler);
        var stages = new List<(RunStage, string?)>();
        host.StageChanged += (stage, detail) => stages.Add((stage, detail));

        var result = await host.RunAsync("int x = 1;", (_, _) => { }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal([.. AllStages, (RunStage.Running, (string?)null)], stages);
    }

    [Fact]
    public async Task AFailedCompile_Should_EndAtTheStageItFailedIn_AndNeverReportRunning()
    {
        var failed = new ProcessCompilerResult("out.dll", false,
            [new ResultItem("NU1101", "", new LineSymbol(0, 0), "Unable to find package 'Nope'")], []);
        var compiler = new StagedCompiler(failed, (RunStage.RestoringPackages, "1 package"));
        var host = CreateHost(compiler);
        var stages = new List<RunStage>();
        var output = new List<string>();
        host.StageChanged += (stage, _) => stages.Add(stage);

        var result = await host.RunAsync("int x = 1;", (text, _) => output.Add(text), TestContext.Current.CancellationToken);

        Assert.Equal(RunOutcome.CompilationFailed, result.Outcome);
        Assert.Equal([RunStage.RestoringPackages], stages);
        Assert.Contains(output, line => line.Contains("Unable to find package 'Nope'"));
    }

    [Fact]
    public async Task ACancelledCompile_Should_Propagate_AndStopReportingStages()
    {
        var compiler = new StagedCompiler(new ProcessCompilerResult("out.dll", true, [], []), AllStages);
        var host = CreateHost(compiler);
        var stages = new List<RunStage>();
        host.StageChanged += (stage, _) => stages.Add(stage);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync("int x = 1;", (_, _) => { }, cts.Token));

        Assert.Empty(stages);
        Assert.Equal(0, compiler.Subscribers); // the host does not stay subscribed to a finished compile
    }

    [Fact]
    public async Task CompileAsync_Should_ReportTheCompilersStages_WithoutRunning()
    {
        var compiler = new StagedCompiler(new ProcessCompilerResult("out.dll", true, [], []), AllStages);
        var host = CreateHost(compiler);
        var stages = new List<RunStage>();
        host.StageChanged += (stage, _) => stages.Add(stage);

        await host.CompileAsync("int x = 1;", TestContext.Current.CancellationToken);

        Assert.Equal([RunStage.RestoringPackages, RunStage.GeneratingConnections, RunStage.Compiling], stages);
    }
}
