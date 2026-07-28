using RunPad.Build.Core.Models;
using RunPad.Build.Core.ProcessRunners;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// TODO.md #111 (bucket 1, Util.CurrentQueryPath) — an environment variable rather than another
// positional CLI argument (see ChildProcess.CurrentQueryPathEnvVar's own comment: RpadKind.Statements/
// Expression compile to top-level statements, which have an implicit `string[] args` in scope, so an
// extra argument would leak into the user's own args). This tests ForExecution's ProcessStartInfo
// directly (StartInfoForTests, internal + InternalsVisibleTo — RunPad.Build.Core has no dedicated test
// project of its own, see AssemblyInfo.cs's own comment) rather than starting a real process.
public class ChildProcessCurrentQueryPathTests
{
    private static ExecutionOptions CreateOptions() => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
        Debugger = new DebuggerOptions { DebuggerPath = "netcoredbg" },
    };

    [Fact]
    public void ForExecution_Should_SetTheEnvironmentVariable_WhenCurrentQueryPathIsGiven()
    {
        var process = ChildProcess.ForExecution(
            CreateOptions(), "out.dll", "123", currentQueryPath: "/root/a.rpad");

        Assert.Equal("/root/a.rpad", process.StartInfoForTests.Environment[ChildProcess.CurrentQueryPathEnvVar]);
    }

    [Fact]
    public void ForExecution_Should_NotSetTheEnvironmentVariable_WhenCurrentQueryPathIsNull()
    {
        var process = ChildProcess.ForExecution(CreateOptions(), "out.dll", "123", currentQueryPath: null);

        Assert.False(process.StartInfoForTests.Environment.ContainsKey(ChildProcess.CurrentQueryPathEnvVar));
    }
}
