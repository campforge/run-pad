using System.Diagnostics;

namespace DotPad.Services.Setup;

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

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
