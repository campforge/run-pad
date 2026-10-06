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

// docs/handoffs/macos-debug-launch.md — end-to-end regression for "breakpoints never hit on macOS":
// real MsBuildProcessCompiler (through CSharpLanguageHost) → real DapSession → real netcoredbg, with
// the output directory reached through a symlinked ancestor (like /var → /private/var for the
// default temp directory), a Program-kind script (non-zero line offset) and Stop while paused.
// Opt-in: needs a netcoredbg executable in RUNPAD_TEST_NETCOREDBG (never downloaded or installed
// by the test); skipped otherwise. Category=RealBuild like every other real `dotnet build` test.
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public class RealNetcoredbgDebugTests : IDisposable
{
    private const string NetcoredbgEnvVar = "RUNPAD_TEST_NETCOREDBG";

    // User lines 1-based; the breakpoint is on `x++;` (line 4).
    private const string Code = """
        void Main()
        {
            var x = 41;
            x++;
            Console.WriteLine($"value={x}");
        }
        """;

    private const int BreakpointLine = 4;

    private readonly string _root;
    private readonly string _aliasOutputDir;

    public RealNetcoredbgDebugTests()
    {
        _root = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "runpad-real-dbg-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_root, "real", "out"));
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));
        _aliasOutputDir = Path.Combine(_root, OperatingSystem.IsWindows() ? "real" : "alias", "out");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class TestBreakpoint(string fileName, int line) : IBreakpoint
    {
        public string FileName { get; } = fileName;
        public int Line { get; set; } = line;
        public string? Condition => null;
        public string? HitCondition => null;
    }

    private CSharpLanguageHost CreateHost(string netcoredbg)
    {
        var sdk = new DotnetSdkProvider().Get().First();
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = sdk,
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = _aliasOutputDir,
                Runtime = new RuntimeOptions { RuntimePath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = netcoredbg },
            },
            OutputDirectory = _aliasOutputDir,
            RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
            Kind = RpadKind.Program,
        };
        var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        // Same path EditorTabItemControl gives its ScriptBreakpoints: OutputDirectory/RunPadQuery.cs.
        host.AddBreakpoint(new TestBreakpoint(Path.Combine(_aliasOutputDir, "RunPadQuery.cs"), BreakpointLine));
        return host;
    }

    private static string RequireNetcoredbg()
    {
        var path = Environment.GetEnvironmentVariable(NetcoredbgEnvVar);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path), $"Set {NetcoredbgEnvVar} to a netcoredbg executable.");
        return path!;
    }

    [Fact]
    public async Task RunDebugAsync_Should_HitTheBreakpoint_ThroughASymlinkedOutputDirectory_AndContinue()
    {
        var netcoredbg = RequireNetcoredbg();
        await using var host = CreateHost(netcoredbg);
        var output = new List<(string Data, OutputKind Kind)>();
        var hit = new TaskCompletionSource<IBreakpointHitContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BreakpointHit += ctx =>
        {
            hit.TrySetResult(ctx);
            ctx.Resume(DebugAction.Continue);
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await host.RunDebugAsync(Code, (data, kind) => { lock (output) output.Add((data, kind)); }, timeout.Token);

        string AllOutput() { lock (output) return string.Join('\n', output.Select(o => $"{o.Kind}: {o.Data}")); }
        Assert.True(hit.Task.IsCompletedSuccessfully, "Breakpoint was never hit. Output:\n" + AllOutput());
        var context = await hit.Task;
        Assert.Equal(BreakpointLine, context.Breakpoint.Line);
        Assert.IsNotType<RunPad.Debug.SyntheticBreakpoint>(context.Breakpoint);
        Assert.Equal(BreakpointLine, context.CallStack[0].Line);
        Assert.Contains(context.LocalVariables, v => v.Name == "x" && v.Value == "41");
        Assert.Contains("value=42", AllOutput());
    }

    [Fact]
    public async Task RunDebugAsync_Should_EndPromptly_WhenStoppedWhilePausedAtABreakpoint()
    {
        var netcoredbg = RequireNetcoredbg();
        await using var host = CreateHost(netcoredbg);
        var output = new List<string>();
        var hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BreakpointHit += _ => hit.TrySetResult(); // never resumed — the user presses Stop instead

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runTask = host.RunDebugAsync(Code, (data, _) => { lock (output) output.Add(data); }, runCts.Token);

        var first = await Task.WhenAny(hit.Task, runTask, Task.Delay(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        Assert.True(first == hit.Task, "Breakpoint was never hit. Output:\n" + string.Join('\n', output));

        await host.TerminateAsync(CancellationToken.None); // what the Stop button calls
        var ended = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.True(ended == runTask, "RunDebugAsync didn't end after Stop.");
        if (runTask.IsFaulted)
            Assert.IsAssignableFrom<OperationCanceledException>(runTask.Exception!.InnerException);
        lock (output) Assert.DoesNotContain(output, o => o.Contains("value=42"));
    }
}
