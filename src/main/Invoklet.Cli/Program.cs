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

var stdout = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Out) });
var stderr = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

return await CliRunner.RunAsync(args, stdout, stderr, cts.Token);
