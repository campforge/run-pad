using DotPad.Build.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;
using DotPad.Build.Events;
using DotPad.Build.Models;
using DotPad.Build.Models.Options;
using DotPad.Build.ProcessCompiler;
using DotPad.Build.ProcessRunners;
using DotPad.Build.ProcessRunners.Args;
using DotPad.Build.ProcessRunners.Interfaces;
using Microsoft.CodeAnalysis.Text;

namespace DotPad.Build;

public sealed class CSharpExecutionHostService : IExecutionHostService, IDisposable
{
    const string LibName = "DotPadQuery";

    private readonly string _outputDir;
    private readonly string _runtimeLib;
    private readonly string _sourceCodePath;
    private readonly Encoding _encoding = Encoding.UTF8;
    private readonly ExecutionPlatform _executionPlatform;

    public event ExecutionReceivedEventHandler? DataReceived;

    public CSharpExecutionHostService(
        string root,
        string runtimeLib,
        ExecutionPlatform executionPlatform
    )
    {
        var direcroryName = Path.GetRandomFileName();

        _outputDir = Path.Combine(root, direcroryName);
        Directory.CreateDirectory(_outputDir);
        _sourceCodePath = Path.Combine(_outputDir, $"{LibName}.cs");
        _executionPlatform = executionPlatform;
        _runtimeLib = runtimeLib;
    }

    private SyntaxTree BuildSyntaxTree(string code)
    {
        var encoding = Encoding.UTF8;
        var buffer = encoding.GetBytes(code);
        var sourceText = SourceText.From(buffer, buffer.Length, _encoding, canBeEmbedded: true);

        var syntaxTree = CSharpSyntaxTree.ParseText(
            text: sourceText,
            new CSharpParseOptions(),
            Path.GetFileName(_sourceCodePath)
        );
        return syntaxTree;
    }

    private async Task RunProcessDebugAsync(CancellationToken cancellationToken)
    {
        await using IExecutionProcess process = new LocalDebuggerProcess(
            new ExecutionOptions()
            {
                WorkingDirectory = _outputDir,
                CreateWindow = true,
                Runtime = new RuntimeOptions
                {
                    RuntimePath = "C:\\Program Files\\dotnet\\dotnet.exe"
                },
                Debugger = new DebuggerOptions()
                {
                    DebuggerPath = @"C:\Users\user\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe"
                }
            }, new LocalDebuggerExecutionArgs()
        );

        //await using var client = new LocalDebuggerProcessClient(process);
        

        var cb = async (IExecutionProcess p, CancellationToken ct) =>
        {
            while (await p.Reader.WaitToReadAsync(ct))
            {
                var line = await p.Reader.ReadAsync(ct);
                DataReceived?.Invoke(this, new ExecutionReceivedEventArgs(line));
            }
        };

        var ecb = async (IExecutionProcess p, CancellationToken ct) =>
        {
            var stringBuilder = new StringBuilder();
            while (await p.ErrorReader.WaitToReadAsync(ct))
            {
                var line = await p.ErrorReader.ReadAsync(ct);
                stringBuilder.AppendLine(line);
            }

            DataReceived?.Invoke(this, new ExecutionReceivedEventArgs(stringBuilder.ToString()));
        };

        var ecs = process.Start(cancellationToken);

        await Task.WhenAll(
            cb(process, cancellationToken),
            ecb(process, cancellationToken),
            ecs
        );

        await process.WaitForExit(cancellationToken);
        await process.Close(cancellationToken);
    }
    
    private async Task RunProcessAsync(string binary,ExecutionOptions executionOptions, CancellationToken cancellationToken)
    {
        await using IExecutionProcess process = new LocalProcess(
            executionOptions
           , new LocalExecutionArgs(binary, Environment.ProcessId.ToString())
        );

        var cb = async (IExecutionProcess p, CancellationToken ct) =>
        {
            while (await p.Reader.WaitToReadAsync(ct))
            {
                var line = await p.Reader.ReadAsync(ct);
                DataReceived?.Invoke(this, new ExecutionReceivedEventArgs(line));
            }
        };

        var ecb = async (IExecutionProcess p, CancellationToken ct) =>
        {
            var stringBuilder = new StringBuilder();
            while (await p.ErrorReader.WaitToReadAsync(ct))
            {
                var line = await p.ErrorReader.ReadAsync(ct);
                stringBuilder.AppendLine(line);
            }

            DataReceived?.Invoke(this, new ExecutionReceivedEventArgs(stringBuilder.ToString()));
        };

        var ecs = process.Start(cancellationToken);

        await Task.WhenAll(
            cb(process, cancellationToken),
            ecb(process, cancellationToken),
            ecs
        );

        await process.WaitForExit(cancellationToken);
        await process.Close(cancellationToken);
    }

    public async Task Execute(
        string code,
        string[] refPaths,
        CancellationToken cancellationToken
    )
    {
        var syntaxTree = BuildSyntaxTree(code);
        var executionOptions = new ExecutionOptions()
        {
            WorkingDirectory = _outputDir, // ToDo
            Runtime = new RuntimeOptions
            {
                RuntimePath = "C:\\Program Files\\dotnet\\dotnet.exe"
            }
        };

        using var compiler = 
            new DotnetProcessCompiler(
                _executionPlatform,
                executionOptions,
                _outputDir,
                _runtimeLib,
                LibName
            );
        // using var compiler = new CodeAnalysisProcessCompiler(
        //     _executionPlatform,
        //     executionOptions,
        //     _outputDir,
        //     _runtimeLib,
        //     LibName
        // );

        var result = await compiler.Compilation(syntaxTree, cancellationToken);

        await RunProcessAsync(result.OutputBinaryFile, executionOptions, cancellationToken);
    }

    public async Task Terminate()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}

