using Invoklet.Build.Core.Models;
using Invoklet.Build.Core.ProcessRunners;
using Xunit;

namespace Invoklet.NonUI.Tests.Build.Core.ProcessRunners;

// An environment variable rather than another
// positional CLI argument (see ChildProcess.CurrentQueryPathEnvVar's own comment: MdRunKind.Statements/
// Expression compile to top-level statements, which have an implicit `string[] args` in scope, so an
// extra argument would leak into the user's own args). This tests ForExecution's ProcessStartInfo
// directly (StartInfoForTests, internal + InternalsVisibleTo — Invoklet.Build.Core has no dedicated test
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
            CreateOptions(), "out.dll", "123", currentQueryPath: "/root/a.inv");

        Assert.Equal("/root/a.inv", process.StartInfoForTests.Environment[ChildProcess.CurrentQueryPathEnvVar]);
    }

    [Fact]
    public void ForExecution_Should_NotSetTheEnvironmentVariable_WhenCurrentQueryPathIsNull()
    {
        var process = ChildProcess.ForExecution(CreateOptions(), "out.dll", "123", currentQueryPath: null);

        Assert.False(process.StartInfoForTests.Environment.ContainsKey(ChildProcess.CurrentQueryPathEnvVar));
    }

    [Fact]
    public void ForExecution_Should_PassTheScriptArguments_AfterTheAssembly_OneItemEach()
    {
        var process = ChildProcess.ForExecution(
            CreateOptions(), "/tmp/my dir/out.dll", "123", arguments: ["a b", "c\"d", ""]);

        Assert.Equal(["/tmp/my dir/out.dll", "a b", "c\"d", ""], process.StartInfoForTests.ArgumentList);
    }

    [Fact]
    public void ForExecution_Should_PassOnlyTheAssembly_WhenThereAreNoArguments()
    {
        var process = ChildProcess.ForExecution(CreateOptions(), "out.dll", "123");

        Assert.Equal(["out.dll"], process.StartInfoForTests.ArgumentList);
    }
}
