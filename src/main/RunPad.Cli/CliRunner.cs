using System.Text;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using Spectre.Console;

namespace RunPad.Cli;

// Compiles and runs a .rpad file's code the same "fast path" the GUI's non-debug Run
// button uses (ILanguageHost.RunAsync, see EditorTabItemControl.RunAsync/BuildRunOptions) — no
// Avalonia, no DAP/netcoredbg. Structured as a static class (rather than top-level statements in
// Program.cs) so the orchestration itself is unit-testable via its IAnsiConsole/CancellationToken
// parameters, without needing a real console — stdout/stderr are IAnsiConsole (not a plain
// TextWriter) so .Dump() output can be rendered as a real Spectre.Console Table/Markup instead of
// plain text (user request — see DumpHtmlSpectreRenderer).
public static class CliRunner
{
    public static Task<int> RunAsync(
        string[] args, IAnsiConsole stdout, IAnsiConsole stderr, CancellationToken cancellationToken)
        => RunAsync(args, CliServices.CreateDefault(), stdout, stderr, cancellationToken);

    public static async Task<int> RunAsync(
        string[] args, CliServices services, IAnsiConsole stdout, IAnsiConsole stderr, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            WriteUsage(stderr);
            return NotRunExitCode;
        }

        // The GUI's "File → Import LINQPad Query..." equivalent
        // for scripting/batch use. Unlike the GUI, overwrites the destination outright rather than
        // picking a free "Foo 2.rpad" — a CLI tool re-run with the same arguments should be idempotent,
        // not silently rename around a leftover file from a previous run.
        if (string.Equals(args[0], "convert", StringComparison.OrdinalIgnoreCase))
            return await ConvertLinqAsync(args, stdout, stderr, cancellationToken);

        var cli = CliArguments.Parse(args);
        if (cli.Error is not null)
        {
            stderr.WriteLine(cli.Error);
            return NotRunExitCode;
        }
        if (cli.ShowHelp)
        {
            WriteUsage(stdout);
            return 0;
        }
        if (cli.ShowVersion)
        {
            stdout.WriteLine(typeof(CliRunner).Assembly.GetName().Version?.ToString(3) ?? "unknown");
            return 0;
        }
        if (cli.ScriptPath is null)
        {
            WriteUsage(stderr);
            return NotRunExitCode;
        }

        var filePath = Path.GetFullPath(cli.ScriptPath);
        if (!File.Exists(filePath))
        {
            stderr.WriteLine($"File not found: {filePath}");
            return NotRunExitCode;
        }

        var raw = await File.ReadAllTextAsync(filePath, cancellationToken);
        var parsed = RpadParser.Parse(raw);
        if (!parsed.IsSuccess)
        {
            stderr.WriteLine($"Failed to parse '{filePath}' as a .rpad document: {parsed.Error}");
            return NotRunExitCode;
        }

        var document = parsed.Document;
        if (cli.Language is { } language && language != document.FrontMatter.Language)
        {
            stderr.WriteLine($"'{filePath}' is a {document.FrontMatter.Language.ToLanguageId()} script, not {language.ToLanguageId()}.");
            return NotRunExitCode;
        }
        if (cli.Kind is { } kind) document.FrontMatter.Kind = kind;
        if (cli.TargetSdk is { } targetSdk) document.FrontMatter.TargetSdk = targetSdk;

        var languageId = document.FrontMatter.Language.ToLanguageId();

        var registry = services.Registry;
        if (!registry.IsRegistered(languageId))
        {
            stderr.WriteLine($"No runner registered for language '{languageId}'.");
            return NotRunExitCode;
        }

        // The SDK the script's targetSdk names — the same rule as the RunPad window.
        var platform = TargetSdkResolver.Resolve(services.Sdks.Get(), document.FrontMatter.TargetSdk);
        if (platform is null)
        {
            stderr.WriteLine("No .NET SDK found (checked `dotnet --list-sdks`).");
            return NotRunExitCode;
        }
        if (TargetSdkResolver.Warning(document.FrontMatter.TargetSdk, platform) is { } sdkWarning)
            stderr.WriteLine("warning: " + sdkWarning);

        var outputDirectory = Directory.CreateTempSubdirectory("runpad-cli-").FullName;
        try
        {
            // A gRPC server's classes are part of the build: read the ones not read yet or that failed.
            if (services.ConnectionSchemas is { } schemas && document.FrontMatter.Connections.Count > 0)
                await schemas.EnsureAsync(document.FrontMatter.Connections, ConnectionSchemaRefresh.Failed, cancellationToken);
            var request = BuildRequest(document.FrontMatter, platform, outputDirectory, filePath, cli.ScriptArguments,
                services.ConnectionDrivers, services.ConnectionSchemas?.Cache);

            RunResult result;
            await using var host = registry.Create(languageId, request);
            try
            {
                if (cli.CompileOnly)
                {
                    var compilation = await host.CompileAsync(document.RunCode, cancellationToken);
                    foreach (var diagnostic in compilation.Diagnostics)
                    {
                        var console = diagnostic.Severity == DiagnosticSeverity.Error ? stderr : stdout;
                        console.WriteLine(FormatDiagnostic(diagnostic));
                    }
                    return compilation.Success ? 0 : NotRunExitCode;
                }

                result = await host.RunAsync(document.RunCode, (data, outputKind) =>
                {
                    switch (outputKind)
                    {
                        case OutputKind.StandardOutput:
                            stdout.WriteLine(data);
                            break;
                        case OutputKind.StandardError:
                            stderr.WriteLine(data);
                            break;
                        case OutputKind.DumpHtml:
                            var html = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                            stdout.Write(DumpHtmlSpectreRenderer.Render(html));
                            break;
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Conventional shell exit code for a SIGINT-cancelled process (128 + SIGINT's 2).
                return 130;
            }

            if (result.Outcome == RunOutcome.StartFailed)
                stderr.WriteLine("Failed to start the compiled script.");
            return ExitCodeFor(result);
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); }
            catch (IOException) { /* best-effort cleanup — a locked file here isn't worth failing the run over */ }
        }
    }

    // The exit code when the script never ran: a bad command line, file or SDK, or a compile error —
    // the same 2 as NetPad's npad, so a caller can tell it from a script that ran and failed.
    internal const int NotRunExitCode = 2;

    private static void WriteUsage(IAnsiConsole console)
    {
        console.WriteLine("Usage: runpad [run] [options] <script.rpad> [--] [script arguments...]");
        console.WriteLine("       runpad convert <input.linq> [output.rpad]");
        console.WriteLine();
        console.WriteLine("Options:");
        console.WriteLine("  -lang=<kind>, -k, --kind <kind>   Statements, Program or Expression (s, p, e)");
        console.WriteLine("  -s, --sdk <version>               The .NET SDK to run on, such as 9 or net9.0");
        console.WriteLine("  -compileonly, --compile-only      Compile the script without running it");
        console.WriteLine("  -h, --help                        Show this help");
        console.WriteLine("  --version                         Show the version");
        console.WriteLine();
        console.WriteLine("What follows the script is passed to it as args, but the options above are");
        console.WriteLine("still read there until --; everything after -- goes to the script as it is.");
    }

    private static string FormatDiagnostic(IDiagnostic d)
    {
        var position = d.Line > 0 ? $"({d.Line},{d.Column}): " : "";
        var code = d.Code.Length > 0 ? $" {d.Code}" : "";
        return $"{position}{d.Severity.ToString().ToLowerInvariant()}{code}: {d.Message}";
    }

    // The script's own exit code is the CLI's, so `Environment.Exit(7)` gives 7 and text on
    // stderr alone (a warning, a log line) doesn't fail the run. Not compiling or not starting is 2.
    internal static int ExitCodeFor(RunResult result)
        => result.Outcome == RunOutcome.Exited ? result.ExitCode ?? 1 : NotRunExitCode;

    internal static async Task<int> ConvertLinqAsync(
        string[] args, IAnsiConsole stdout, IAnsiConsole stderr, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            stderr.WriteLine("Usage: runpad convert <input.linq> [output.rpad]");
            return 1;
        }

        var linqPath = Path.GetFullPath(args[1]);
        if (!File.Exists(linqPath))
        {
            stderr.WriteLine($"File not found: {linqPath}");
            return 1;
        }

        var outputPath = args.Length >= 3
            ? Path.GetFullPath(args[2])
            : Path.Combine(Path.GetDirectoryName(linqPath)!, Path.GetFileNameWithoutExtension(linqPath) + ".rpad");

        RpadDocument document;
        try
        {
            var linqText = await File.ReadAllTextAsync(linqPath, cancellationToken);
            document = LinqConverter.Convert(linqText, title: Path.GetFileNameWithoutExtension(linqPath));
        }
        catch (FormatException ex)
        {
            stderr.WriteLine($"Failed to convert '{linqPath}': {ex.Message}");
            return 1;
        }

        await File.WriteAllTextAsync(outputPath, RpadWriter.Write(document), cancellationToken);
        stdout.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    // The run the script's front matter asks for, built as the window builds its own: the language's
    // backend resolves its references (relative to the script, macros expanded) and package sources.
    // Arguments given on the command line replace the front matter's. The process starts in the
    // temporary build folder; the PATH's dotnet runs it, and there is no debugger on this path
    // (ILanguageHost.RunAsync, not RunDebugAsync).
    internal static ExecutionRequest BuildRequest(
        RpadFrontMatter frontMatter, DotnetSdk platform, string outputDirectory,
        string? currentQueryPath = null, IReadOnlyList<string>? arguments = null, ConnectionDriverRegistry? connectionDrivers = null,
        ConnectionSchemaCache? connectionSchemas = null)
        => ExecutionRequest.For(frontMatter, currentQueryPath, arguments) with
        {
            Connections = GeneratedConnections.For(frontMatter.Connections, frontMatter.Language, frontMatter.Packages.Keys,
                connectionDrivers ?? new ConnectionDriverRegistry(), connectionSchemas),
            Platform = platform,
            OutputDirectory = outputDirectory,
            WorkingDirectory = outputDirectory,
        };
}
