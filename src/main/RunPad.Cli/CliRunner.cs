using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.VB;
using Spectre.Console;

namespace RunPad.Cli;

// TODO.md #40: compiles and runs a .rpad file's code the same "fast path" the GUI's non-debug Run
// button uses (ILanguageHost.RunAsync, see EditorTabItemControl.RunAsync/BuildRunOptions) — no
// Avalonia, no DAP/netcoredbg. Structured as a static class (rather than top-level statements in
// Program.cs) so the orchestration itself is unit-testable via its IAnsiConsole/CancellationToken
// parameters, without needing a real console — stdout/stderr are IAnsiConsole (not a plain
// TextWriter) so .Dump() output can be rendered as a real Spectre.Console Table/Markup instead of
// plain text (user request — see DumpHtmlSpectreRenderer).
public static class CliRunner
{
    public static async Task<int> RunAsync(
        string[] args, IAnsiConsole stdout, IAnsiConsole stderr, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("Usage: runpad <path-to.rpad>");
            stderr.WriteLine("       runpad convert <input.linq> [output.rpad]");
            return 1;
        }

        // TODO.md #63 — the GUI's "File → Import LINQPad Query..." (девяносто третий проход) equivalent
        // for scripting/batch use. Unlike the GUI, overwrites the destination outright rather than
        // picking a free "Foo 2.rpad" — a CLI tool re-run with the same arguments should be idempotent,
        // not silently rename around a leftover file from a previous run.
        if (string.Equals(args[0], "convert", StringComparison.OrdinalIgnoreCase))
            return await ConvertLinqAsync(args, stdout, stderr, cancellationToken);

        var filePath = Path.GetFullPath(args[0]);
        if (!File.Exists(filePath))
        {
            stderr.WriteLine($"File not found: {filePath}");
            return 1;
        }

        var raw = await File.ReadAllTextAsync(filePath, cancellationToken);
        var document = RpadParser.TryParse(raw);
        if (document is null)
        {
            stderr.WriteLine($"Failed to parse '{filePath}' as a .rpad document.");
            return 1;
        }

        var languageId = document.FrontMatter.Language.ToLanguageId();

        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance));

        if (!registry.IsRegistered(languageId))
        {
            stderr.WriteLine($"No runner registered for language '{languageId}'.");
            return 1;
        }

        var platform = new DotnetSdkProvider().Get().FirstOrDefault();
        if (platform is null)
        {
            stderr.WriteLine("No .NET SDK found (checked `dotnet --list-sdks`).");
            return 1;
        }

        var outputDirectory = Directory.CreateTempSubdirectory("runpad-cli-").FullName;
        try
        {
            var options = BuildOptions(document.FrontMatter, platform, outputDirectory, filePath);

            var hadError = false;
            await using var host = registry.Create(languageId, options);
            try
            {
                await host.RunAsync(document.RunCode, (data, kind) =>
                {
                    switch (kind)
                    {
                        case OutputKind.StandardOutput:
                            stdout.WriteLine(data);
                            break;
                        case OutputKind.StandardError:
                            hadError = true;
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

            return hadError ? 1 : 0;
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); }
            catch (IOException) { /* best-effort cleanup — a locked file here isn't worth failing the run over */ }
        }
    }

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

    // languageId is no longer needed here — every compiled .NET language now shares the same
    // options type (ANALYSIS.md #86/#87); the caller still resolves it separately to pick the
    // ILanguageHostFactory itself (see registry.Create above).
    internal static ILanguageHostOptions BuildOptions(
        RpadFrontMatter frontMatter, DotnetSdk platform, string outputDirectory, string? currentQueryPath = null)
    {
        var runtimeLibPath = new CSharpReferenceService().GetExtensionReference();
        var executionOptions = new ExecutionOptions
        {
            WorkingDirectory = outputDirectory,
            Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
            // No debugger involved on this path (ILanguageHost.RunAsync, not RunDebugAsync) — left
            // empty rather than probing for netcoredbg the GUI's preferences would otherwise supply.
            Debugger = new DebuggerOptions { DebuggerPath = "" },
        };

        var packages = frontMatter.Packages.ToDictionary(p => p.Key, p => p.Value.Version);
        var usings = frontMatter.Namespaces ?? [];
        // $(RuntimeDirectory)/$(MyDocuments)/$(CurrentQueryPath) macros (TODO.md #63) — must stay
        // equivalent to EditorTabItemControl.BuildRunOptions's own expansion (see CliRunnerTests.cs).
        var assemblyReferences = frontMatter.References?
            .Where(r => r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(r => ReferencePathMacros.Expand(r, currentQueryPath))
            .ToList() ?? [];
        var projectReferences = frontMatter.References?
            .Where(r => r.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || r.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            .Select(r => ReferencePathMacros.Expand(r, currentQueryPath))
            .ToList() ?? [];

        // One options type for every compiled .NET language (ANALYSIS.md #86/#87) — languageId no
        // longer needs to select between two identical option-object shapes here.
        return new CompiledDotnetLanguageHostOptions
        {
            Platform = platform,
            ExecutionOptions = executionOptions,
            OutputDirectory = outputDirectory,
            RuntimeLibPath = runtimeLibPath,
            Kind = frontMatter.Kind,
            Packages = packages,
            Usings = usings,
            AssemblyReferences = assemblyReferences,
            ProjectReferences = projectReferences,
            CurrentQueryPath = currentQueryPath,
        };
    }
}
