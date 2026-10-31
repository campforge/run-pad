using Invoklet.Build.Core.Models;
using Invoklet.Build.Core.ProcessRunners;
using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invoklet.NonUI.Tests.Languages.DotnetCommon.Fixtures;

// A host whose compiler always succeeds and whose process exits at once, so a test sees only what the host does around them.
internal sealed class CompiledDotnetLanguageHostFixture : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), "Invoklet-tests-" + Guid.NewGuid());

    public int SecretKeyExports { get; private set; }

    public CSharpLanguageHost CreateSut(string? connectionSource = null, bool withExporter = true)
    {
        var options = new CompiledDotnetLanguageHostOptions
        {
            Platform = new DotnetSdk("net10.0", "net10.0", "10.0.0", "net10.0"),
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = _outputDir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "netcoredbg" },
            },
            OutputDirectory = _outputDir,
            RuntimeLibPath = "Invoklet.Runtime.dll",
            Kind = MdRunKind.Statements,
            ConnectionSource = connectionSource,
        };
        var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance, (_, _, _) => new SucceedingCompiler(),
            executionProcessFactory: (_, _, _, _, _) => new ExitedProcess());
        if (withExporter) host.ExportSecretKey = () => SecretKeyExports++;
        return host;
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true);
    }

    private sealed class SucceedingCompiler : IDotnetProcessCompiler
    {
        public event StageChangedHandler? StageChanged
        {
            add { }
            remove { }
        }

        public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx)
            => Task.FromResult(new ProcessCompilerResult("out.dll", true, [], []));

        public void Dispose()
        {
        }
    }

    private sealed class ExitedProcess : IChildProcess
    {
        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public Stream OutputStream { get; } = new MemoryStream();
        public Stream InputStream { get; } = new MemoryStream();
        public bool Start() => true;

        public void Kill()
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
        public int ExitCode => 0;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
