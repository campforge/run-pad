using System.Diagnostics;
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
    public static IEnumerable<string> Shell(string commandText, string? args = null, Encoding? encoding = null) =>
        ShellCore(commandText, args, encoding).ToArray();

    private static IEnumerable<string> ShellCore(string commandText, string? args, Encoding? encoding)
    {
        var arguments = SplitArguments(commandText);
        if (!string.IsNullOrEmpty(args))
            arguments.AddRange(SplitArguments(args));

        if (arguments.Count == 0)
            yield break;

        var fileName = arguments[0];
        arguments.RemoveAt(0);

        using var process = StartProcess(fileName, arguments, encoding);
        if (process is null)
            yield break;

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

        if (process.ExitCode != 0)
            throw new CommandExecutionException(errors.ToString(), process.ExitCode);
    }

    // No shell involved, so no quoting rules to worry about — this only needs to match how the
    // (currently sole) caller passes a plain space-separated command like "dotnet --list-sdks".
    private static List<string> SplitArguments(string text) =>
        text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static Process? StartProcess(string fileName, IReadOnlyList<string> arguments, Encoding? encoding)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };

        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);

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
