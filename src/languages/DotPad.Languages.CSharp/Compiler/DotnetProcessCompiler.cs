using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DotPad.Build.Core.Models;
using Microsoft.CodeAnalysis;
using ExecutionPlatform = DotPad.Contracts.Models.ExecutionPlatform;
using Microsoft.Extensions.Logging;

namespace DotPad.Languages.CSharp.Compiler;

public sealed class DotnetProcessCompiler : ProcessCompilerBase
{
    private const string BinDir = "bin";

    private readonly string _sourceCodePath;
    private readonly string _fileProjPath;
    private readonly Encoding _encoding = Encoding.UTF8;
    private readonly ILogger<DotnetProcessCompiler> _logger;

    private readonly IReadOnlyDictionary<string, string> _nugetPackages;
    private readonly IReadOnlyList<string> _globalUsings;
    private readonly IReadOnlyList<string> _assemblyReferences;
    private readonly IReadOnlyList<string> _projectReferences;
    private readonly string? _targetFrameworkOverride;

    private readonly Regex _errorRegex = new(@"^([\s\S]+)\((\d+,\d+)\): error (\S+): ([\s\S]+)$");
    private readonly Regex _warningRegex = new(@"^([\s\S]+)\((\d+,\d+)\): warning (\S+): ([\s\S]+)$");

    private Process? _process;

    public DotnetProcessCompiler(
        ExecutionPlatform executionPlatform,
        ExecutionOptions executionOptions,
        string outputDir,
        string dotPadRuntimeLib,
        string outputName,
        IReadOnlyDictionary<string, string> nugetPackages,
        IReadOnlyList<string> globalUsings,
        IReadOnlyList<string> assemblyReferences,
        IReadOnlyList<string> projectReferences,
        string? targetFrameworkOverride,
        ILogger<DotnetProcessCompiler> logger)
        : base(executionPlatform, executionOptions, outputDir, dotPadRuntimeLib, outputName)
    {
        _nugetPackages = nugetPackages;
        _globalUsings = globalUsings;
        _assemblyReferences = assemblyReferences;
        _projectReferences = projectReferences;
        _targetFrameworkOverride = targetFrameworkOverride;
        _logger = logger;
        _sourceCodePath = Path.Combine(OutputDir, $"{OutputName}.cs");
        _fileProjPath = Path.Combine(OutputDir, $"{OutputName}.csproj");
    }

    public override async Task<ProcessCompilerResult> Compilation(
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        var code = (await syntaxTree.GetTextAsync(cancellationToken)).ToString();

        // Prepend global usings from .rpad front matter
        if (_globalUsings.Count > 0)
        {
            var usingsText = string.Join("\n", _globalUsings.Select(u => $"global using {u};"));
            code = usingsText + "\n" + code;
        }

        await File.WriteAllTextAsync(_sourceCodePath, code, _encoding, cancellationToken);

        var tfm = _targetFrameworkOverride ?? ExecutionPlatform.TargetFrameworkMoniker;
        var allAssemblyRefs = new[] { DotPadRuntimeLib }.Concat(_assemblyReferences).ToArray();
        var projectFile = Resources.GerProjectFile(tfm, allAssemblyRefs, _projectReferences, _nugetPackages);
        await File.WriteAllTextAsync(_fileProjPath, projectFile, _encoding, cancellationToken);

        var binDirectory = Path.Join(OutputDir, BinDir);
        Directory.CreateDirectory(binDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutionOptions.Runtime.RuntimePath,
            Arguments = @$"build -o ""./{BinDir}"" -c Debug --nologo --no-dependencies --tl:off",
            WorkingDirectory = OutputDir,
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
            throw new Exception("DotnetProcessCompiler failed to start");

        var errors = new List<ResultItem>();
        var warnings = new List<ResultItem>();

        // BUG-1: stderr не читается конкурентно со stdout.
        // Если dotnet build запишет много данных в stderr (ошибки MSBuild при загрузке NuGet),
        // пайп заполнится, процесс зависнет, а ReadLineAsync ниже никогда не вернёт null → дедлок.
        // Правильно: Task.WhenAll(DrainStdout, DrainStderr) с параллельным чтением.
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            _logger.LogDebug("{Line}", line);

            var errorMatch = _errorRegex.Match(line);
            if (errorMatch.Success) { errors.Add(ParseMatch(errorMatch)); continue; }

            var warningMatch = _warningRegex.Match(line);
            if (warningMatch.Success) warnings.Add(ParseMatch(warningMatch));
        }

        await _process.WaitForExitAsync(cancellationToken);

        return new ProcessCompilerResult(
            Path.Combine(binDirectory, $"{OutputName}.dll"),
            _process.ExitCode == 0,
            errors,
            warnings);
    }

    public override void Dispose()
    {
        if (_process == null) return;
        if (!_process.HasExited)
        {
            try { _process.Kill(true); }
            catch (Exception) { }
        }
        _process?.Dispose();
    }

    private ResultItem ParseMatch(Match match)
    {
        var file = match.Groups["1"].Value.Replace(OutputDir, "");
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
