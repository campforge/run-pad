using System.Diagnostics;
using System.Text;
using RunPad.Build.Core.Models;

namespace RunPad.Build.Core.ProcessRunners;

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

    // Test-only observability (InternalsVisibleTo) — TODO.md #111, confirms ForExecution actually
    // sets/omits the CurrentQueryPath environment variable, without starting a real process.
    internal ProcessStartInfo StartInfoForTests => _process.StartInfo;

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

    // TODO.md #111 (Util.CurrentQueryPath) — an environment variable, not another positional
    // argument like assemblyPath/parentPid: RpadKind.Statements/Expression compile to C# top-level
    // statements, which have an implicit `string[] args` in scope, so any extra CLI argument here
    // would leak straight into the user's own `args` (already true, if unobserved, of --pid above).
    // An env var is invisible to `args` and read back via RunPad.Runtime.Util.CurrentQueryPath's own
    // Environment.GetEnvironmentVariable — mirrors this same key name, duplicated rather than shared
    // (RunPad.Runtime is netstandard2.0 and has no reference to this assembly), same precedent as
    // CompiledDotnetLanguageHostBase's RsPrefix/InputRequestPrefix mirroring RunPad.Runtime.Util's.
    internal const string CurrentQueryPathEnvVar = "RUNPAD_CURRENT_QUERY_PATH";

    /// <summary>Creates a process for running a compiled .NET assembly.</summary>
    public static ChildProcess ForExecution(
        ExecutionOptions options, string assemblyPath, string parentPid, string? currentQueryPath = null)
    {
        var startInfo = new ProcessStartInfo
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
        };
        if (currentQueryPath is not null)
            startInfo.Environment[CurrentQueryPathEnvVar] = currentQueryPath;

        return new ChildProcess(startInfo);
    }

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
