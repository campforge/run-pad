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

    // Errors/warnings with no source position at all — e.g. "CSC : error CS5001: Program does not
    // contain a static 'Main' method..." or "MSBUILD : error MSB1009: ..." (confirmed against real
    // `dotnet build` output). The positioned regexes above require "(line,col)", so these used to fall
    // through both: ExitCode != 0 (Success = false) with an EMPTY Errors list — the build failed and
    // the user saw nothing at all. Tried only after the positioned regexes miss.
    private readonly Regex _unpositionedErrorRegex = new(@"^(.+?) : error (\S*): ([\s\S]+)$");
    private readonly Regex _unpositionedWarningRegex = new(@"^(.+?) : warning (\S*): ([\s\S]+)$");

    // How many trailing build-output lines go into the fallback diagnostic when a build fails
    // without a single line matching any of the regexes above.
    private const int FallbackOutputTailLines = 10;

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

        // R6: a referenced project's own sources, project file and transitive references are outside
        // everything compared below, so with project references the hot path, --no-restore and
        // --no-dependencies all go — MSBuild's own incremental build/restore check over the whole
        // graph decides what's stale instead (a no-op build when nothing changed).
        var hasProjectReferences = _projectReferences.Count > 0;

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
        if (!hasProjectReferences && projectUnchanged && previousSource == code && File.Exists(outputDll)
            && !IsAnyReferenceNewerThan(outputDll, allAssemblyRefs))
            return new ProcessCompilerResult(outputDll, true, [], []);

        // Rewriting identical text would only bump timestamps and force MSBuild to recompile the
        // script on every run with project references, defeating its incremental check.
        if (previousSource != code)
            await File.WriteAllTextAsync(_sourceCodePath, code, _encoding, ctx);
        if (!projectUnchanged)
            await File.WriteAllTextAsync(_fileProjPath, projectFile, _encoding, ctx);

        // _outputDir is reused across runs of the same tab (see EditorTabState.OutputDirectory) so
        // that MSBuild/NuGet's own incremental caches under obj/ survive between runs. If the
        // project file we just wrote is byte-for-byte identical to what was already there — i.e.
        // packages/references/target framework haven't changed since the last run in this
        // directory — restore already ran for this exact input and can safely be skipped, cutting
        // build time roughly in half for the common "just edited the code" case.
        var canSkipRestore = !hasProjectReferences && projectUnchanged
            && File.Exists(Path.Combine(_outputDir, "obj", "project.assets.json"));

        Directory.CreateDirectory(binDirectory);

        // -nodeReuse:false: a multi-project graph (project references) makes MSBuild start worker
        // nodes, and with node reuse they outlive `dotnet build` while still holding our redirected
        // stdout/stderr — the drains below then never see EOF and Compilation hangs (R6 review,
        // reproduced independently). Only affects the nodes of this build, not other build servers.
        var graphArguments = hasProjectReferences ? " -nodeReuse:false" : " --no-dependencies";
        var startInfo = new ProcessStartInfo
        {
            FileName = _executionOptions.Runtime.RuntimePath,
            Arguments =
                @$"build -o ""./{BinDir}"" -c Debug --nologo{graphArguments} --tl:off{(canSkipRestore ? " --no-restore" : "")}",
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

        var (errors, warnings, outputTail) = taskOut.Result;

        // Last line of defense against "Run did nothing, no error anywhere": a failed build must
        // always surface at least one error, even for an output shape none of the regexes know.
        if (!success && errors.Count == 0)
            errors.Add(CreateUnrecognizedFailureItem(_process.ExitCode, outputTail));

        return new ProcessCompilerResult(outputDll, success, errors, warnings);
    }

    private static ResultItem CreateUnrecognizedFailureItem(int exitCode, IReadOnlyCollection<string> outputTail)
    {
        var message = $"dotnet build failed with exit code {exitCode} without reporting a recognizable error.";
        if (outputTail.Count > 0)
            message += "\n" + string.Join("\n", outputTail);
        return new ResultItem("", "", new LineSymbol(0, 0), message);
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

    private async Task<(List<ResultItem> Errors, List<ResultItem> Warnings, IReadOnlyCollection<string> OutputTail)> DrainStdout(
        CancellationToken cancellationToken)
    {
        var errors = new List<ResultItem>();
        var warnings = new List<ResultItem>();
        // MSBuild prints every error/warning twice — once as it happens, then again in the "Build
        // FAILED." summary at the end (confirmed against real `dotnet build` output) — so without
        // this every diagnostic showed up twice in Results. ResultItem is a record, so value
        // equality is exactly "same code, file, position and message".
        var seen = new HashSet<ResultItem>();
        var outputTail = new Queue<string>();
        while (_process != null && await _process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            _logger.LogDebug("{Line}", line);

            outputTail.Enqueue(line.Trim());
            if (outputTail.Count > FallbackOutputTailLines) outputTail.Dequeue();

            if (TryParseLine(line, _errorRegex, _unpositionedErrorRegex) is { } error)
            {
                if (seen.Add(error)) errors.Add(error);
                continue;
            }

            if (TryParseLine(line, _warningRegex, _unpositionedWarningRegex) is { } warning && seen.Add(warning))
                warnings.Add(warning);
        }

        return (errors, warnings, outputTail);
    }

    private ResultItem? TryParseLine(string line, Regex positioned, Regex unpositioned)
    {
        var match = positioned.Match(line);
        if (match.Success) return ParseMatch(match);

        match = unpositioned.Match(line);
        return match.Success ? ParseUnpositionedMatch(match) : null;
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
        var message = RemoveProjectAnnotation(match.Groups["4"].Value, _fileProjPath);

        var lineSplit = linePart.Split(',');
        return new ResultItem(
            code, file,
            new LineSymbol(uint.Parse(lineSplit[0]), uint.Parse(lineSplit[1])),
            message);
    }

    // Line/column 0 is the "no source position" marker (real positions are 1-based).
    private ResultItem ParseUnpositionedMatch(Match match)
    {
        var origin = match.Groups["1"].Value.Trim();
        var code = match.Groups["2"].Value;
        var message = RemoveProjectAnnotation(match.Groups["3"].Value, _fileProjPath);
        return new ResultItem(code, origin, new LineSymbol(0, 0), message);
    }

    // MSBuild ends every diagnostic with " [<full project path>]". That path isn't always spelled
    // like projectPath: `dotnet build` runs in _outputDir and finds the project via the process
    // cwd, which macOS reports symlink-resolved — projectPath "/var/folders/.../Query.csproj" comes
    // back as "[/private/var/folders/.../Query.csproj]" (confirmed against real output), so an exact
    // Replace left it in every message. Accepts the exact path or that path behind a rooted prefix;
    // only a terminal annotation is removed — other bracketed message text stays untouched.
    internal static string RemoveProjectAnnotation(string message, string projectPath)
    {
        var suffix = projectPath + "]";
        if (!message.EndsWith(suffix, StringComparison.Ordinal)) return message;

        var pathStart = message.Length - suffix.Length;
        var open = message.LastIndexOf(" [", pathStart, StringComparison.Ordinal);
        if (open < 0 || open + 2 > pathStart) return message;

        var prefix = message.AsSpan(open + 2, pathStart - open - 2);
        if (prefix.Length > 0 && prefix[0] is not ('/' or '\\')) return message;

        return message[..open];
    }
}
