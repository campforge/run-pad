using System.Diagnostics;
using System.Text;
using DotPad.Build.Core.Models;

namespace DotPad.Build.Core.ProcessRunners;

/// <summary>
/// Lightweight wrapper over <see cref="Process"/>. Starts a child process and
/// exposes its standard streams. Callers decide how to read/write those streams.
/// </summary>
public sealed class ChildProcess : IChildProcess
{
    private readonly Process _process;

    // ── Text readers (line-based output) ─────────────────────────────────────

    /// <summary>stdout as a text reader (encoding set via <see cref="ProcessStartInfo"/>).</summary>
    public StreamReader StandardOutput => _process.StandardOutput;

    /// <summary>stderr as a text reader (encoding set via <see cref="ProcessStartInfo"/>).</summary>
    public StreamReader StandardError => _process.StandardError;

    // ── Raw binary streams (binary protocols such as DAP) ────────────────────

    /// <summary>Raw stdout stream — read DAP messages here (Content-Length framing).</summary>
    public Stream OutputStream => _process.StandardOutput.BaseStream;

    /// <summary>Raw stdin stream — write DAP messages here (Content-Length framing).</summary>
    public Stream InputStream => _process.StandardInput.BaseStream;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public ChildProcess(ProcessStartInfo startInfo)
    {
        _process = new Process { StartInfo = startInfo };
    }

    /// <summary>Starts the process. Returns false if the process could not be started.</summary>
    public bool Start() => _process.Start();

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Factories ─────────────────────────────────────────────────────────────

    /// <summary>Creates a process for running a compiled .NET assembly.</summary>
    public static ChildProcess ForExecution(ExecutionOptions options, string assemblyPath, string parentPid)
        => new(new ProcessStartInfo
        {
            FileName = options.Runtime.RuntimePath,
            Arguments = new LocalExecutionArgs(assemblyPath, parentPid).ToString(),
            WorkingDirectory = options.WorkingDirectory,
            CreateNoWindow = !options.CreateWindow,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        });

    /// <summary>Creates a process for the netcoredbg DAP debugger.</summary>
    public static ChildProcess ForDebugger(ExecutionOptions options)
        => new(new ProcessStartInfo
        {
            FileName = options.Debugger.DebuggerPath,
            Arguments = "--interpreter=vscode",
            WorkingDirectory = options.WorkingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
            // No StandardOutputEncoding: stdout carries raw DAP binary frames.
        });
}
