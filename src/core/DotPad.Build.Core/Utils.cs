using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DotPad.Build.Core;

[Serializable]
public class CommandExecutionException : Exception
{
    public int ExitCode { get; }
    public string ErrorText { get; }

    public CommandExecutionException(string msg) : base(msg)
    {
        ErrorText = msg;
    }

    public CommandExecutionException(string msg, int exitCode)
        : base(string.IsNullOrEmpty(msg) ? $"The process returned exit code {exitCode}" : msg)
    {
        ErrorText = msg;
        ExitCode = exitCode;
    }
}

public static class Utils
{
    public static IEnumerable<string> Shell(string commandText, string? args = null, Encoding? encoding = null)
    {
        var results = ShellCore(commandText, args, encoding).ToArray();
        return new CmdResults(results);
    }

    private static IEnumerable<string> ShellCore(string commandText, string? args, Encoding? encoding)
    {
        var process = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetWindowsProcess(commandText, args, encoding)
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? GetUnixProcess("/bin/bash", commandText, args, encoding)
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? GetUnixProcess("/bin/bash", commandText, args, encoding)
                    : null;

        if (process is null)
            yield break;

        using (process)
        {
            var errors = new StringBuilder();
            var locker = new object();

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (locker) errors.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();

            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
                yield return line;

            process.WaitForExit();

            if (process.ExitCode != 0 && errors.Length > 0)
                throw new CommandExecutionException(errors.ToString(), process.ExitCode);
        }
    }

    private static Process? GetWindowsProcess(string cmdText, string? args, Encoding? encoding)
    {
        var arguments = BuildArguments(cmdText, args, encoding);
        return StartProcess("cmd.exe", arguments, encoding);
    }

    private static Process? GetUnixProcess(string shell, string cmdText, string? args, Encoding? encoding)
    {
        var arguments = BuildArguments(cmdText, args, encoding);
        return StartProcess(shell, arguments, encoding);
    }

    private static string BuildArguments(string cmdText, string? args, Encoding? encoding)
    {
        cmdText = cmdText.Trim();
        if (cmdText.Contains(' '))
            cmdText = $"\"{cmdText}\"";

        var prefix = encoding?.EncodingName == "Unicode" ? "/u /c" : "/c";
        return string.IsNullOrEmpty(args) ? $"{prefix} {cmdText}" : $"{prefix} {cmdText} {args}";
    }

    private static Process? StartProcess(string fileName, string arguments, Encoding? encoding)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };

        try
        {
            if (Path.IsPathRooted(info.FileName))
            {
                var dir = Path.GetDirectoryName(info.FileName);
                if (Directory.Exists(dir))
                    info.WorkingDirectory = dir;
            }
        }
        catch (ArgumentException) { }

        return Process.Start(info);
    }
}

internal sealed class CmdResults : IEnumerable<string>
{
    private readonly IEnumerable<string> _items;
    internal CmdResults(IEnumerable<string> items) => _items = items;
    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
