using Xunit;

namespace RunPad.Runtime.Tests;

// TODO.md #111 (bucket 1) — LINQPad-inspired Util.Cmd/CmdCore. Uses "dotnet" itself as the external
// process under test (guaranteed present on every CI runner this solution builds on — it's what's
// running the test suite) rather than an OS-specific executable, except for the one test that needs
// to tell stdout and stderr apart, which needs an actual shell (cmd.exe on Windows, /bin/sh elsewhere
// — same OperatingSystem.IsWindows()-gated pattern already used by SaveIfNeededFailureTests/
// MainWindowChromeTests). [Collection(ConsoleTestCollection.Name)]: the plain Cmd(string, string?,
// string?) overload writes to Console.Out/Error, which is process-wide (see ConsoleTestCollection).
[Collection(ConsoleTestCollection.Name)]
public class UtilCmdTests
{
    [Fact]
    public void Cmd_Should_ReturnZero_WhenTheProcessSucceeds()
    {
        var exitCode = Util.Cmd("dotnet", "--version");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Cmd_Should_StreamTheProcesssStdout_ToConsoleOut_Live()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            Util.Cmd("dotnet", "--version");

            Assert.False(string.IsNullOrWhiteSpace(sw.ToString()));
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void Cmd_WithOutOutput_Should_CaptureCombinedOutput_InsteadOfWritingItLive()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var exitCode = Util.Cmd("dotnet", "--version", out var output);

            Assert.Equal(0, exitCode);
            Assert.False(string.IsNullOrWhiteSpace(output));
            Assert.Equal(string.Empty, sw.ToString());
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void CmdCore_Should_InvokeOnOutputLine_ForEachStdoutLine()
    {
        var lines = new List<string>();

        var exitCode = Util.CmdCore("dotnet", "--version", workingDirectory: null, lines.Add, _ => { });

        Assert.Equal(0, exitCode);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void CmdCore_Should_ReturnANonZeroExitCode_WhenTheProcessFails()
    {
        var exitCode = Util.CmdCore(
            "dotnet", "runpad-test-not-a-real-subcommand", workingDirectory: null, _ => { }, _ => { });

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void CmdCore_Should_RouteStdoutAndStderr_ToTheirRespectiveCallbacks()
    {
        string executable;
        string arguments;
        if (OperatingSystem.IsWindows())
        {
            executable = "cmd.exe";
            arguments = "/c \"echo out-line & echo err-line 1>&2\"";
        }
        else
        {
            executable = "/bin/sh";
            arguments = "-c \"echo out-line; echo err-line 1>&2\"";
        }

        var outLines = new List<string>();
        var errLines = new List<string>();

        Util.CmdCore(executable, arguments, workingDirectory: null, outLines.Add, errLines.Add);

        Assert.Contains(outLines, l => l.Contains("out-line"));
        Assert.DoesNotContain(outLines, l => l.Contains("err-line"));
        Assert.Contains(errLines, l => l.Contains("err-line"));
        Assert.DoesNotContain(errLines, l => l.Contains("out-line"));
    }
}
