using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RunPad.Contracts.Models;
using ExecutionOptions = RunPad.Build.Core.Models.ExecutionOptions;

namespace RunPad.Languages.DotnetCommon.Compiler;

/// <summary>
/// Writes a throwaway single-file MSBuild project + source file for one compiled .NET language and
/// shells out to <c>dotnet build</c> — parameterized by <see cref="DotnetLanguageProjectProfile"/>
/// instead of being duplicated per language (was DotnetProcessCompiler + VbProcessCompiler,
/// ~95% identical including both regexes and every comment — ANALYSIS.md #86).
/// </summary>
public sealed class MsBuildProcessCompiler : IDotnetProcessCompiler
{
    private const string BinDir = "bin";

    private readonly DotnetLanguageProjectProfile _profile;
    private readonly DotnetSdk _sdk;
    private readonly ExecutionOptions _executionOptions;
    private readonly string _outputDir;
    private readonly string _runPadRuntimeLib;
    private readonly string _outputName;
    private readonly string _sourceCodePath;
    private readonly string _fileProjPath;
    private readonly Encoding _encoding = Encoding.UTF8;
    private readonly ILogger<MsBuildProcessCompiler> _logger;

    private readonly IReadOnlyDictionary<string, string> _nugetPackages;
    private readonly IReadOnlyList<string> _usings;
    private readonly IReadOnlyList<string> _assemblyReferences;
    private readonly IReadOnlyList<string> _projectReferences;

    // Bug report 2026-07-27: a "'latest' is not a valid version string" NuGet restore failure has NO
    // diagnostic code at all — real output is "...NuGet.targets(196,5): error : 'latest' is not a
    // valid version string." (confirmed by actually forcing this exact failure) — the code group used
    // to require \S+ (one or more), so that line simply never matched either regex. The build's
    // ExitCode still came back non-zero (Success correctly false), but with an EMPTY Diagnostics list,
    // so RunAsync's `if (!compilationResult.Success ...) return;` silently did nothing: no error
    // anywhere, no execution — exactly "nothing happens when I hit Run". \S* (zero or more) admits an
    // empty code without affecting the normal "error CS0103: ..." case.
    private readonly Regex _errorRegex = new(@"^([\s\S]+)\((\d+,\d+)\): error (\S*): ([\s\S]+)$");
    private readonly Regex _warningRegex = new(@"^([\s\S]+)\((\d+,\d+)\): warning (\S*): ([\s\S]+)$");

    private Process? _process;

    public MsBuildProcessCompiler(
        DotnetLanguageProjectProfile profile,
        DotnetSdk sdk,
        ExecutionOptions executionOptions,
        string outputDir,
        string runPadRuntimeLib,
        string outputName,
        IReadOnlyDictionary<string, string> nugetPackages,
        IReadOnlyList<string> usings,
        IReadOnlyList<string> assemblyReferences,
        IReadOnlyList<string> projectReferences,
        ILogger<MsBuildProcessCompiler> logger)
    {
        _profile = profile;
        _sdk = sdk;
        _executionOptions = executionOptions;
        _outputDir = outputDir;
        _runPadRuntimeLib = runPadRuntimeLib;
        _outputName = outputName;
        _nugetPackages = nugetPackages;
        _usings = usings;
        _assemblyReferences = assemblyReferences;
        _projectReferences = projectReferences;
        _logger = logger;
        _sourceCodePath = Path.Combine(_outputDir, $"{_outputName}{_profile.SourceFileExtension}");
        _fileProjPath = Path.Combine(_outputDir, $"{_outputName}{_profile.ProjectFileExtension}");
    }

    public async Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx)
    {
        // Prepend global usings/imports from .rpad front matter, in this language's own syntax.
        if (_usings.Count > 0)
        {
            var importsText = string.Join("\n", _usings.Select(_profile.FormatImportStatement));
            code = importsText + "\n" + code;
        }

        var tfm = _sdk.TargetFrameworkMoniker;
        var allAssemblyRefs = new[] { _runPadRuntimeLib }.Concat(_assemblyReferences).ToArray();
        var projectFile = BuildProjectFile(tfm, allAssemblyRefs, _projectReferences, _nugetPackages);

        var binDirectory = Path.Join(_outputDir, BinDir);
        var outputDll = Path.Combine(binDirectory, $"{_outputName}.dll");

        var previousSource = File.Exists(_sourceCodePath) ? await File.ReadAllTextAsync(_sourceCodePath, ctx) : null;
        var previousProjectFile = File.Exists(_fileProjPath) ? await File.ReadAllTextAsync(_fileProjPath, ctx) : null;
        var projectUnchanged = previousProjectFile == projectFile;

        // Hot path: source, project file and the binary from the last build in this directory are
        // all still exactly what they were — nothing to recompile, so skip `dotnet build` entirely
        // rather than just skipping restore. Also requires outputDll to be at least as new as every
        // referenced assembly (runPadRuntimeLib/assemblyReferences) — live user report (TODO.md
        // #111, "Util.CurrentQueryPath always null"): the generated project file only records a
        // reference's PATH (the <HintPath> written below), not its content, so rebuilding
        // RunPad.Runtime.dll in place (e.g. updating RunPad itself) was invisible to projectUnchanged
        // — same path string, so the hot path fired anyway and kept serving whatever stale
        // RunPad.Runtime.dll copy happened to be sitting in this output directory's bin/ from the
        // last time it was actually built, silently running old runtime code against unchanged
        // script source.
        if (projectUnchanged && previousSource == code && File.Exists(outputDll)
            && !IsAnyReferenceNewerThan(outputDll, allAssemblyRefs))
            return new ProcessCompilerResult(outputDll, true, [], []);

        await File.WriteAllTextAsync(_sourceCodePath, code, _encoding, ctx);
        await File.WriteAllTextAsync(_fileProjPath, projectFile, _encoding, ctx);

        // _outputDir is reused across runs of the same tab (see EditorTabState.OutputDirectory) so
        // that MSBuild/NuGet's own incremental caches under obj/ survive between runs. If the
        // project file we just wrote is byte-for-byte identical to what was already there — i.e.
        // packages/references/target framework haven't changed since the last run in this
        // directory — restore already ran for this exact input and can safely be skipped, cutting
        // build time roughly in half for the common "just edited the code" case.
        var canSkipRestore = projectUnchanged && File.Exists(Path.Combine(_outputDir, "obj", "project.assets.json"));

        Directory.CreateDirectory(binDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executionOptions.Runtime.RuntimePath,
            Arguments =
                @$"build -o ""./{BinDir}"" -c Debug --nologo --no-dependencies --tl:off{(canSkipRestore ? " --no-restore" : "")}",
            WorkingDirectory = _outputDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        _process = new Process();
        _process.StartInfo = startInfo;

        if (!_process.Start())
            throw new InvalidOperationException("MsBuildProcessCompiler failed to start");

        var taskOut = DrainStdout(ctx);
        var taskErr = DrainStderr(ctx);

        await Task.WhenAll(taskOut, taskErr);
        await _process.WaitForExitAsync(ctx);

        var success = _process.ExitCode == 0;

        // A failed build must not leave a stale, previously-successful outputDll sitting in bin/ —
        // MSBuild only overwrites it on a successful compile, so a failing build otherwise leaves
        // the OLD binary in place. Without this, the next run with this same (still broken) source
        // hits the hot path above (source/project file unchanged, outputDll still exists) and
        // silently reruns the old binary instead of retrying the build or reporting the same error
        // again.
        if (!success) TryDeleteStaleOutput(outputDll);

        return new ProcessCompilerResult(
            outputDll,
            success,
            taskOut.Result.Item1,
            taskOut.Result.Item2);
    }

    private string BuildProjectFile(
        string targetFramework,
        string[] assemblyReferences,
        IReadOnlyList<string> projectReferences,
        IReadOnlyDictionary<string, string> nugetPackages)
    {
        var extraProperties = string.Concat(_profile.ExtraPropertyGroupElements.Select(e => $"\n        {e}"));

        var imports = _profile.DefaultImportNamespaces.Select(ns =>
            $"""
                    <{_profile.ImportItemElementName} Include="{SecurityElement.Escape(ns)}" />
            """);

        var asmRefs = assemblyReferences.Select(r =>
            $"""
                    <Reference Include="{SecurityElement.Escape(Path.GetFileNameWithoutExtension(r))}">
                        <HintPath>{SecurityElement.Escape(r)}</HintPath>
                    </Reference>
            """);

        var projRefs = projectReferences.Select(r =>
            $"""
                    <ProjectReference Include="{SecurityElement.Escape(r)}" />
            """);

        var pkgRefs = nugetPackages.Select(kv =>
            $"""
                    <PackageReference Include="{SecurityElement.Escape(kv.Key)}" Version="{SecurityElement.Escape(ResolvePackageVersion(kv.Value))}" />
            """);

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>{targetFramework}</TargetFramework>{extraProperties}
                    <EmbedAllSources>true</EmbedAllSources>
                </PropertyGroup>
                <ItemGroup>
            {string.Join("\n", imports)}
            {string.Join("\n", asmRefs)}
            {string.Join("\n", projRefs)}
            {string.Join("\n", pkgRefs)}
                </ItemGroup>
            </Project>
            """;
    }

    // Bug report 2026-07-27: RpadFrontMatter's Package.Default (and any package added via the
    // Dependencies window without pinning a version) uses the literal string "latest" — meaningful
    // app-side vocabulary (also what gets written into the .rpad YAML front matter), but NOT valid
    // NuGet syntax: writing it straight into Version="latest" made `dotnet build` fail outright with
    // "'latest' is not a valid version string" (confirmed by actually building a project with it).
    // NuGet's real "always take the newest published version" spelling is the floating-version
    // wildcard "*" — this is the one place real NuGet XML gets generated from that sentinel, so it's
    // the right place to translate it.
    private static string ResolvePackageVersion(string version) =>
        string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase) ? "*" : version;

    private void TryDeleteStaleOutput(string outputDll)
    {
        try
        {
            if (File.Exists(outputDll)) File.Delete(outputDll);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete stale build output {OutputDll} after a failed build", outputDll);
        }
    }

    // TODO.md #111 fix (live user report, "Util.CurrentQueryPath always null") — the hot-path skip
    // above only compares generated TEXT (project file/source), which references an assembly like
    // RunPad.Runtime.dll by PATH, not content; this catches the case that text comparison can't:
    // the referenced file itself got rebuilt in place since outputDll was last actually produced.
    // Swallows missing-reference errors rather than throwing — a reference that's gone entirely will
    // fail loudly in the real `dotnet build` this forces anyway, with a much more useful error.
    private static bool IsAnyReferenceNewerThan(string outputDll, IReadOnlyList<string> references)
    {
        var outputWriteTimeUtc = File.GetLastWriteTimeUtc(outputDll);
        foreach (var reference in references)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(reference) > outputWriteTimeUtc) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return false;
    }

    private async Task<(List<ResultItem>, List<ResultItem>)> DrainStdout(CancellationToken cancellationToken)
    {
        var errors = new List<ResultItem>();
        var warnings = new List<ResultItem>();
        while (_process != null && await _process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            _logger.LogDebug("{Line}", line);
            var errorMatch = _errorRegex.Match(line);
            if (errorMatch.Success)
            {
                errors.Add(ParseMatch(errorMatch));
                continue;
            }

            var warningMatch = _warningRegex.Match(line);
            if (warningMatch.Success) warnings.Add(ParseMatch(warningMatch));
        }

        return (errors, warnings);
    }

    private async Task DrainStderr(CancellationToken cancellationToken)
    {
        while (_process != null && await _process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            if (!string.IsNullOrWhiteSpace(line))
                _logger.LogWarning("build stderr: {Line}", line);
    }

    public void Dispose()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(true);
        }
        catch (Exception)
        {
            // ignored
        }

        _process?.Dispose();
    }

    private ResultItem ParseMatch(Match match)
    {
        var file = match.Groups["1"].Value.Replace(_outputDir, "");
        var linePart = match.Groups["2"].Value;
        var code = match.Groups["3"].Value;
        var message = match.Groups["4"].Value.Replace($" [{_fileProjPath}]", "");

        var lineSplit = linePart.Split(',');
        return new ResultItem(
            code, file,
            new LineSymbol(uint.Parse(lineSplit[0]), uint.Parse(lineSplit[1])),
            message);
    }
}
