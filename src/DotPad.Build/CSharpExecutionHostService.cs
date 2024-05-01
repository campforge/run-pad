using DotPad.Build.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Diagnostics;
using System.Text;
using DotPad.Build.Events;
using DotPad.Build.Models;

namespace DotPad.Build;

public sealed class CSharpExecutionHostService : IExecutionHostService, IDisposable
{
    private readonly bool _IsRun = false;
    public bool IsRun => _IsRun;
    const string libName = "DotPadQuery";
    private string _binaryOutput;
    private string _pbbOutput;
    private string _configOutput;
    private string _outputDir;
    private ExecutionPlatform _executionPlatform;

    public event ExecutionReceivedEventHandler DataReceived;

    public CSharpExecutionHostService(
        string root,
        ExecutionPlatform executionPlatform
    )
    {
        string direcroryName = Path.GetRandomFileName();

        _outputDir = Path.Combine(root, direcroryName);
        Directory.CreateDirectory(_outputDir);

        _binaryOutput = Path.Combine(_outputDir, $"{libName}.dll");
        _pbbOutput = Path.Combine(_outputDir, $"{libName}.pdb");
        _configOutput = Path.Combine(_outputDir, $"{libName}.runtimeconfig.json");
        _executionPlatform = executionPlatform;

    }

    private SyntaxTree BuildSyntaxTree(string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);
        return syntaxTree;
    }

    private async Task<EmitResult> Compilation(

        SyntaxTree syntaxTree,
        string[] refPaths,
        CancellationToken cancellationToken
    )
    {
        var references = refPaths.Select(r => MetadataReference.CreateFromFile(r)).ToArray();

        var task = Task.Run(() =>
        {
            var compilation = CSharpCompilation
                .Create(Guid.NewGuid().ToString(),
                    syntaxTrees: new[] { syntaxTree },
                    references: references,
                    options: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                );

            var json = @"
			{
			  ""runtimeOptions"": {
				""tfm"": """ + _executionPlatform.TargetFrameworkMoniker + @""",
				""framework"": {
				  ""name"": ""Microsoft.NETCore.App"",
				  ""version"": """ + $"{_executionPlatform.Version.Major}.{_executionPlatform.Version.Minor}.0" + @"""
				},
				""configProperties"": {
				  ""System.Runtime.InteropServices.BuiltInComInterop.IsSupported"": true
				}
			  }
			}";

            File.WriteAllText(_configOutput, json);
            File.Copy(@"E:\Projects2\DotPad\DotPad.Runtime\bin\Debug\net7.0\DotPad.Runtime.dll",
                Path.Combine(_outputDir, "DotPad.Runtime.dll"), true);

            return compilation.Emit(
                _binaryOutput,
                _pbbOutput,
                cancellationToken: cancellationToken);
        }, cancellationToken);

        return await task;
    }


    private async Task RunProcessAsync(CancellationToken cancellationToken)
    {
        var pid = Environment.ProcessId;
        using var process = new Process { StartInfo = GetProcessStartInfo(_binaryOutput) };

        using var _ = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex)
            {
            }
        });


        if (!process.Start())
        {
            return;
        }

        StringBuilder errors = new StringBuilder();
        var locker = new object();
        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs errorArgs)
        {
            if (errorArgs.Data != null)
            {
                lock (locker)
                {
                    errors.AppendLine(errorArgs.Data);
                }
            }
        };

        process.BeginErrorReadLine();

        await ReadObjectProcessStreamAsync(process.StandardOutput, cancellationToken);

        await process.WaitForExitAsync();

        int exitCode = process.ExitCode;
        if (exitCode != 0 && errors.Length > 0)
        {
            lock (locker)
            {
                throw new Exception(errors.ToString());
            }
        }

        ProcessStartInfo GetProcessStartInfo(string assemblyPath) => new()
        {
            FileName = @"C:\Program Files\dotnet\dotnet.exe",
            Arguments = $" \"{assemblyPath}\" --pid {Environment.ProcessId}",
            WorkingDirectory = _outputDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
    }


    public async Task Execute(
        string code,
        string[] refPaths,
        CancellationToken cancellationToken
    )
    {
        var syntaxTree = BuildSyntaxTree(code);

        var result = await Compilation(syntaxTree, refPaths, cancellationToken);

        if (!result.Success)
        {
            var failures = result.Diagnostics.Where(diagnostic =>
                diagnostic.IsWarningAsError ||
                diagnostic.Severity == DiagnosticSeverity.Error);

            foreach (Diagnostic diagnostic in failures)
            {
                Console.Error.WriteLine("\t{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
            }

            return;
        }

        await RunProcessAsync(cancellationToken);
    }

    public async Task Terminate()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }


    private async Task ReadObjectProcessStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            DataReceived.Invoke(this, new ExecutionReceivedEventArgs(line));
        }
    }
}

