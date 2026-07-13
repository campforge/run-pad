using System.Text;
using DotPad.Build.Core.Models;
using DotPad.Contracts;
using DotPad.Contracts.Models;
using DotPad.Format.RPad;
using DotPad.Languages.CSharp;
using DotPad.Languages.CSharp.Sdk;
using DotPad.Languages.VB;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace DotPad.Cli;

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
            stderr.WriteLine("Usage: dotpad <path-to.rpad>");
            return 1;
        }

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

        // Mirrors EditorTabItemControl.ResolveLanguageId exactly — relies on RpadLanguage's member
        // spelling matching each ILanguageHostFactory.Language id (case-insensitively).
        var languageId = document.FrontMatter.Language.ToString().ToLowerInvariant();

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

        var outputDirectory = Directory.CreateTempSubdirectory("dotpad-cli-").FullName;
        try
        {
            var options = BuildOptions(document.FrontMatter, languageId, platform, outputDirectory);

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

    internal static ILanguageHostOptions BuildOptions(
        RpadFrontMatter frontMatter, string languageId, DotnetSdk platform, string outputDirectory)
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
        var assemblyReferences = frontMatter.References?
            .Where(r => r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var projectReferences = frontMatter.References?
            .Where(r => r.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || r.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        if (languageId == "vb")
        {
            return new VbLanguageHostOptions
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
            };
        }

        return new CSharpLanguageHostOptions
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
        };
    }
}
