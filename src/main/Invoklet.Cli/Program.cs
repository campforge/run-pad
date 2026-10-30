using Invoklet.Build.Core.Secrets;
using Invoklet.Cli;
using Spectre.Console;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let RunAsync's own cancellation-token plumbing unwind (kill the child process, clean up the
    // temp output dir) instead of the runtime hard-killing this process on the spot.
    e.Cancel = true;
    cts.Cancel();
};

// Util.GetSecret here and in the script reads an encrypted Secret Store with the key Invoklet keeps
// in the system keychain.
SecretKeys.ExportForScripts(SecretKeys.DefaultStorePath());
// Nothing here answers a script's input requests yet: a missing secret is reported, not asked for.
Environment.SetEnvironmentVariable(Invoklet.Build.Core.ProcessRunners.ChildProcess.InputEnvVar, "0");

var stdout = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Out) });
var stderr = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

return await CliRunner.RunAsync(args, stdout, stderr, cts.Token);
