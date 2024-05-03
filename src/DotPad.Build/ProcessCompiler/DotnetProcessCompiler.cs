using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DotPad.Build.Models;
using DotPad.Build.Models.Options;
using DotPad.Build.ProcessCompiler.Models;
using Microsoft.CodeAnalysis;

namespace DotPad.Build.ProcessCompiler;

public sealed class DotnetProcessCompiler : ProcessCompilerBase
{
    private const string BinDir = "bin";

    private readonly string _sourceCodePath;
    private readonly string _fileProjPath;
    private readonly Encoding _encoding = Encoding.UTF8;

    private readonly Regex _errorRegex = new(@"^([\s\S]+)\((\d+,\d+)\): error (\S+): ([\s\S]+)$");
    private readonly Regex _warningRegex = new(@"^([\s\S]+)\((\d+,\d+)\): warning (\S+): ([\s\S]+)$");

    private Process? _process;
    
    public DotnetProcessCompiler(
        ExecutionPlatform executionPlatform,
        ExecutionOptions executionOptions,
        string outputDir, 
        string dotPadRuntimeLib, 
        string outputName) : base(executionPlatform, executionOptions, outputDir, dotPadRuntimeLib, outputName)
    {
        _sourceCodePath = Path.Combine(OutputDir, $"{OutputName}.cs");
        _fileProjPath = Path.Combine(OutputDir, $"{OutputName}.csproj"); 
    }

    public override async Task<ProcessCompilerResult> Compilation(
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        var code = await syntaxTree.GetTextAsync(cancellationToken);
        await File.WriteAllTextAsync(_sourceCodePath, code.ToString(), _encoding, cancellationToken);
        var references = new[] { DotPadRuntimeLib };
        var projectFile = Resources.GerProjectFile(ExecutionPlatform.TargetFrameworkMoniker, references);

        await File.WriteAllTextAsync(_fileProjPath, projectFile, _encoding, cancellationToken);
        var binDirectory = Path.Join(OutputDir, BinDir);
        Directory.CreateDirectory(binDirectory);
        
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutionOptions.Runtime.RuntimePath,
            Arguments = @$"build -o ""./{BinDir}"" -c Debug --nologo --no-dependencies --tl:off", // ToDo: --no-restore
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
            throw new Exception("DotnetProcessCompiler"); //

        var errors = new List<ResultItem>();
        var warning = new List<ResultItem>();

        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrEmpty(line))
                break;

            Console.WriteLine(line);

            var errorMatch = _errorRegex.Match(line);
            if (errorMatch.Success)
            {
                errors.Add(ParseMatch(errorMatch));
                continue;
            }

            var warningMatch = _warningRegex.Match(line);
            if (!warningMatch.Success) 
                continue;
            
            warning.Add(ParseMatch(warningMatch));
        }

        await _process.WaitForExitAsync(cancellationToken);

        return new ProcessCompilerResult(
            Path.Combine(binDirectory, $"{OutputName}.dll"),
            _process.ExitCode == 0,
            errors,
            warning
        );
    }

    public override void Dispose()
    {
        if (_process == null) return;
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(true);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error kill proces");
            }
        }
        _process?.Dispose();
    }

    private ResultItem ParseMatch(Match match)
    {

        var file = match.Groups["1"].Value;
        var line = match.Groups["2"].Value;
        var code = match.Groups["3"].Value;
        var message = match.Groups["4"].Value;

        message = message.Replace($" [{_fileProjPath}]", "");
        file = file.Replace(OutputDir, "");
        var lineSplit = line.Split(',');

        var result = new ResultItem(
            code,
            file,
            new LineSymbol(uint.Parse(lineSplit[0]), uint.Parse(lineSplit[1])),
            message
        );
        return result;
    }
}


