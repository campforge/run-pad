using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DotPad.Build.Core.Models;
using DotPad.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.VB.Compiler;

public sealed class VbProcessCompiler : IDisposable
{
    private const string BinDir = "bin";

    private readonly DotnetSdk _sdk;
    private readonly ExecutionOptions _executionOptions;
    private readonly string _outputDir;
    private readonly string _dotPadRuntimeLib;
    private readonly string _outputName;
    private readonly string _sourceCodePath;
    private readonly string _fileProjPath;
    private readonly Encoding _encoding = Encoding.UTF8;
    private readonly ILogger<VbProcessCompiler> _logger;

    private readonly IReadOnlyDictionary<string, string> _nugetPackages;
    private readonly IReadOnlyList<string> _usings;
    private readonly IReadOnlyList<string> _assemblyReferences;
    private readonly IReadOnlyList<string> _projectReferences;

    private readonly Regex _errorRegex = new(@"^([\s\S]+)\((\d+,\d+)\): error (\S+): ([\s\S]+)$");
    private readonly Regex _warningRegex = new(@"^([\s\S]+)\((\d+,\d+)\): warning (\S+): ([\s\S]+)$");

    private Process? _process;

    public VbProcessCompiler(
        DotnetSdk sdk,
        ExecutionOptions executionOptions,
        string outputDir,
        string dotPadRuntimeLib,
        string outputName,
        IReadOnlyDictionary<string, string> nugetPackages,
        IReadOnlyList<string> usings,
        IReadOnlyList<string> assemblyReferences,
        IReadOnlyList<string> projectReferences,
        ILogger<VbProcessCompiler> logger)
    {
        _sdk = sdk;
        _executionOptions = executionOptions;
        _outputDir = outputDir;
        _dotPadRuntimeLib = dotPadRuntimeLib;
        _outputName = outputName;
        _nugetPackages = nugetPackages;
        _usings = usings;
        _assemblyReferences = assemblyReferences;
        _projectReferences = projectReferences;
        _logger = logger;
        _sourceCodePath = Path.Combine(_outputDir, $"{_outputName}.vb");
        _fileProjPath = Path.Combine(_outputDir, $"{_outputName}.vbproj");
    }

    public async Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx)
    {
        // Prepend global imports from .rpad front matter (VB's "using" equivalent — no semicolon,
        // statement separator is newline).
        if (_usings.Count > 0)
        {
            var importsText = string.Join("\n", _usings.Select(u => $"Imports {u}"));
            code = importsText + "\n" + code;
        }

        var tfm = _sdk.TargetFrameworkMoniker;
        var allAssemblyRefs = new[] { _dotPadRuntimeLib }.Concat(_assemblyReferences).ToArray();
        var projectFile = Resources.GetProjectFile(tfm, allAssemblyRefs, _projectReferences, _nugetPackages);

        var binDirectory = Path.Join(_outputDir, BinDir);
        var outputDll = Path.Combine(binDirectory, $"{_outputName}.dll");

        var previousSource = File.Exists(_sourceCodePath) ? await File.ReadAllTextAsync(_sourceCodePath, ctx) : null;
        var previousProjectFile = File.Exists(_fileProjPath) ? await File.ReadAllTextAsync(_fileProjPath, ctx) : null;
        var projectUnchanged = previousProjectFile == projectFile;

        // Hot path: source, vbproj and the binary from the last build in this directory are all
        // still exactly what they were — nothing to recompile, so skip `dotnet build` entirely
        // rather than just skipping restore.
        if (projectUnchanged && previousSource == code && File.Exists(outputDll))
            return new ProcessCompilerResult(outputDll, true, [], []);

        await File.WriteAllTextAsync(_sourceCodePath, code, _encoding, ctx);
        await File.WriteAllTextAsync(_fileProjPath, projectFile, _encoding, ctx);

        // _outputDir is reused across runs of the same tab so MSBuild/NuGet's own incremental caches
        // under obj/ survive between runs — if the .vbproj we just wrote is byte-for-byte identical
        // to what was already there, restore already ran for this exact input and can be skipped.
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
            throw new Exception("VbProcessCompiler failed to start");

        var taskOut = DrainStdout(ctx);
        var taskErr = DrainStderr(ctx);

        await Task.WhenAll(taskOut, taskErr);
        await _process.WaitForExitAsync(ctx);

        return new ProcessCompilerResult(
            Path.Combine(binDirectory, $"{_outputName}.dll"),
            _process.ExitCode == 0,
            taskOut.Result.Item1,
            taskOut.Result.Item2);
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
