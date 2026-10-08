using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// docs/handoffs/roslyn-script-compiler.md — the hard requirement of the switch to in-process Roslyn:
// netcoredbg must debug Roslyn-built scripts. Expectations match what the same scenarios gave with the
// old MSBuild builds (verified side by side before MSBuild was removed). Real DapSession + real
// netcoredbg; opt-in via RUNPAD_TEST_NETCOREDBG like RealNetcoredbgDebugTests.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class RoslynCompilerDebugTests : IDisposable
{
    private const string NetcoredbgEnvVar = "RUNPAD_TEST_NETCOREDBG";

    private const string ProgramCode = """
        void Main()
        {
            var x = 41;
            x++;
            Console.WriteLine($"value={x}");
        }
        """;

    private const string StatementsCode = """
        var x = 41;
        x++;
        Console.WriteLine($"value={x}");
        """;

    private readonly string _outputDir = Directory.CreateTempSubdirectory("runpad-roslyn-dbg").FullName;

    public void Dispose() => Directory.Delete(_outputDir, recursive: true);

    private sealed class TestBreakpoint(string fileName, int line) : IBreakpoint
    {
        public string FileName { get; } = fileName;
        public int Line { get; set; } = line;
        public string? Condition => null;
        public string? HitCondition => null;
    }

    private static string RequireNetcoredbg()
    {
        var path = Environment.GetEnvironmentVariable(NetcoredbgEnvVar);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path), $"Set {NetcoredbgEnvVar} to a netcoredbg executable.");
        return path!;
    }

    private CSharpLanguageHost CreateHost(string netcoredbg, RpadKind kind, int breakpointLine)
    {
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdkProvider().Get().First(),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = _outputDir,
                Runtime = new RuntimeOptions { RuntimePath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = netcoredbg },
            },
            OutputDirectory = _outputDir,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = kind,
        };
        var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        host.AddBreakpoint(new TestBreakpoint(Path.Combine(_outputDir, "RunPadQuery.cs"), breakpointLine));
        return host;
    }

    [Theory]
    [InlineData(RpadKind.Program, 4)]
    [InlineData(RpadKind.Statements, 2)]
    public async Task RoslynBuiltScript_Should_HitTheBreakpoint_ShowLocals_AndContinue(RpadKind kind, int line)
    {
        await using var host = CreateHost(RequireNetcoredbg(), kind, line);
        var output = new List<string>();
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BreakpointHit += ctx =>
        {
            hit.TrySetResult(ctx);
            ctx.Resume(DebugAction.Continue);
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await host.RunDebugAsync(kind == RpadKind.Program ? ProgramCode : StatementsCode,
            (data, k) => { lock (output) output.Add($"{k}: {data}"); }, timeout.Token);

        string All() { lock (output) return string.Join('\n', output); }
        Assert.True(hit.Task.IsCompletedSuccessfully, "Breakpoint was never hit. Output:\n" + All());
        var context = await hit.Task;
        Assert.Equal(line, context.Breakpoint.Line);
        Assert.IsNotType<RunPad.Debug.SyntheticBreakpoint>(context.Breakpoint);
        Assert.Equal(line, context.CallStack[0].Line);
        Assert.Contains(context.LocalVariables, v => v.Name == "x" && v.Value == "41");
        Assert.Contains("value=42", All());
    }

    [Fact]
    public async Task RoslynBuiltScript_Should_StepOverToTheNextLine()
    {
        await using var host = CreateHost(RequireNetcoredbg(), RpadKind.Program, 4);
        var stops = new List<(int Line, string? X)>();
        host.BreakpointHit += ctx =>
        {
            lock (stops) stops.Add((ctx.CallStack[0].Line, ctx.LocalVariables.FirstOrDefault(v => v.Name == "x")?.Value));
            ctx.Resume(stops.Count == 1 ? DebugAction.StepOver : DebugAction.Continue);
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await host.RunDebugAsync(ProgramCode, (_, _) => { }, timeout.Token);

        lock (stops)
        {
            Assert.True(stops.Count >= 2, $"Expected a breakpoint stop and a step stop, got {stops.Count}.");
            Assert.Equal((4, "41"), stops[0]);
            Assert.Equal((5, "42"), stops[1]);
        }
    }

    [Fact]
    public async Task RoslynBuiltScript_Should_EndPromptly_WhenStoppedWhilePaused()
    {
        await using var host = CreateHost(RequireNetcoredbg(), RpadKind.Program, 4);
        var output = new List<string>();
        var hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BreakpointHit += _ => hit.TrySetResult();

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runTask = host.RunDebugAsync(ProgramCode, (data, _) => { lock (output) output.Add(data); }, runCts.Token);

        var first = await Task.WhenAny(hit.Task, runTask, Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        Assert.True(first == hit.Task, "Breakpoint was never hit. Output:\n" + string.Join('\n', output));

        await host.TerminateAsync(CancellationToken.None);
        var ended = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.True(ended == runTask, "RunDebugAsync didn't end after Stop.");
        lock (output) Assert.DoesNotContain(output, o => o.Contains("value=42"));
    }

    // Watch panel / conditional breakpoints: netcoredbg evaluates expressions from the PDB + metadata.
    [Fact]
    public async Task RoslynBuiltScript_Should_EvaluateExpressions_WhilePaused()
    {
        await using var host = CreateHost(RequireNetcoredbg(), RpadKind.Program, 4);
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BreakpointHit += ctx => hit.TrySetResult(ctx);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var run = host.RunDebugAsync(ProgramCode, (_, _) => { }, timeout.Token);

        var context = await hit.Task.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        var sum = await context.EvaluateAsync("x + 1", TestContext.Current.CancellationToken);
        var text = await context.EvaluateAsync("x * 2", TestContext.Current.CancellationToken);
        context.Resume(DebugAction.Continue);
        await run;

        // Method-call chains such as x.ToString().Length fail in netcoredbg's own evaluator — identically for
        // MSBuild-built scripts when they were compared, so it's an evaluator limit, not a build one.
        Assert.Equal("42", sum?.Value);
        Assert.Equal("82", text?.Value);
    }

    // Break on uncaught exceptions (on by default): the stop lands on the throwing line of the script.
    [Fact]
    public async Task RoslynBuiltScript_Should_StopOnAnUnhandledException_AtTheThrowingLine()
    {
        const string code = """
            void Main()
            {
                var x = 41;
                if (x > 0)
                    throw new InvalidOperationException("boom");
            }
            """;
        await using var host = CreateHost(RequireNetcoredbg(), RpadKind.Program, 100); // no real breakpoint line
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BreakpointHit += ctx =>
        {
            hit.TrySetResult(ctx);
            ctx.Resume(DebugAction.Continue);
        };

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        runCts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await host.RunDebugAsync(code, (_, _) => { }, runCts.Token);
        }
        catch (Exception)
        {
            // The script itself fails with the exception after Continue — only the stop matters here.
        }

        Assert.True(hit.Task.IsCompletedSuccessfully, "The debugger never stopped on the unhandled exception.");
        var context = await hit.Task;
        Assert.Equal(5, context.CallStack[0].Line);
        Assert.Contains(context.LocalVariables, v => v.Name == "x" && v.Value == "41");
    }

    // VB scripts are built by Roslyn too (VbScriptCompiler): same breakpoint/locals/continue path, with
    // VB's Statements wrapper (Module/Sub Main, 2 lines) in the line mapping.
    [Fact]
    public async Task RoslynBuiltVbScript_Should_HitTheBreakpoint_ShowLocals_AndContinue()
    {
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdkProvider().Get().First(),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = _outputDir,
                Runtime = new RuntimeOptions { RuntimePath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = RequireNetcoredbg() },
            },
            OutputDirectory = _outputDir,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = RpadKind.Statements,
        };
        await using var host = new RunPad.Languages.VB.VbLanguageHost(options, NullLoggerFactory.Instance);
        host.AddBreakpoint(new TestBreakpoint(Path.Combine(_outputDir, "RunPadQuery.vb"), 2));
        var output = new List<string>();
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BreakpointHit += ctx =>
        {
            hit.TrySetResult(ctx);
            ctx.Resume(DebugAction.Continue);
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await host.RunDebugAsync("Dim x = 41\nx += 1\nConsole.WriteLine(\"value=\" & x)\n",
            (data, _) => { lock (output) output.Add(data); }, timeout.Token);

        Assert.True(hit.Task.IsCompletedSuccessfully, "Breakpoint was never hit. Output:\n" + string.Join('\n', output));
        var context = await hit.Task;
        Assert.Equal(2, context.CallStack[0].Line);
        Assert.Contains(context.LocalVariables, v => v.Name == "x" && v.Value == "41");
        lock (output) Assert.Contains(output, o => o.Contains("value=42"));
    }
}
