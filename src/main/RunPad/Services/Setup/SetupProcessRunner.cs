using System.Diagnostics;

namespace RunPad.Services.Setup;

public sealed class SetupProcessRunner : ISetupProcessRunner
{
    public async Task<int> RunAsync(
        string fileName, IReadOnlyList<string> arguments, IProgress<string>? progress, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) progress?.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) progress?.Report(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelling (Cancel, or the install timeout) used to stop only the wait: the installer
            // kept running, and the wizard let a second install start next to it. End the installer
            // and whatever it started (the install scripts download with curl/PowerShell), and wait
            // for that before the wizard is free again.
            await StopProcessTreeAsync(process);
            throw;
        }
        return process.ExitCode;
    }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private static async Task StopProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to stop any more.
        }

        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Bounded: a process that won't die within the timeout doesn't hang the wizard.
        }
    }
}
