using System.Diagnostics;
using Invoklet.Services.Setup;
using Xunit;

namespace Invoklet.NonUI.Tests;

public class SetupProcessRunnerTests
{
    // Cancelling used to stop only the wait: the installer kept running, and a second install could
    // start next to it. The process (and its children) must be gone when RunAsync gives up.
    [Fact]
    public async Task Cancelling_Should_StopTheProcessTree()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "$PID; Start-Sleep -Seconds 60" })
            : ("sh", new[] { "-c", "echo $$; sleep 60" });
        // A generous wait: PowerShell can take long to start when the machine is busy (full test runs).
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SyncProgress(line =>
        {
            if (int.TryParse(line.Trim(), out var id)) pid.TrySetResult(id);
        });
        using var cts = new CancellationTokenSource();

        var run = new SetupProcessRunner().RunAsync(fileName, arguments, progress, cts.Token);
        var processId = await pid.Task.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(IsRunning(processId), $"process {processId} is still running");
    }

    [Fact]
    public async Task AFinishedProcess_Should_ReturnItsExitCode()
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "exit 3" })
            : ("sh", new[] { "-c", "exit 3" });

        var exitCode = await new SetupProcessRunner().RunAsync(fileName, arguments, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, exitCode);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Progress<T> posts to the captured context; the test needs the line as soon as it's read.
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
