using System.Diagnostics;
using System.Text;
using Invoklet.Build.Core.Models;

namespace Invoklet.Build.Core.ProcessRunners;

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

    // Test-only observability (InternalsVisibleTo) — confirms ForExecution actually
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

    public int ExitCode => _process.ExitCode;

    // Set for a script run (ForExecution): the file whose appearance cancels the script's
    // Util.CancellationToken.
    private string? _cancelFilePath;

    public bool RequestStop()
    {
        if (_cancelFilePath is null) return false;
        try
        {
            File.WriteAllText(_cancelFilePath, "");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }
        _process.Dispose();
        if (_cancelFilePath is not null)
        {
            try { File.Delete(_cancelFilePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        GC.SuppressFinalize(this);
    }

    // ── Factories ─────────────────────────────────────────────────────────────

    // An environment variable, not another positional
    // argument like assemblyPath/parentPid: MdRunKind.Statements/Expression compile to C# top-level
    // statements, which have an implicit `string[] args` in scope, so any extra CLI argument here
    // would leak straight into the user's own `args` (already true, if unobserved, of --pid above).
    // An env var is invisible to `args` and read back via Invoklet.Runtime.Util.CurrentQueryPath's own
    // Environment.GetEnvironmentVariable — mirrors this same key name, duplicated rather than shared
    // (Invoklet.Runtime is netstandard2.0 and has no reference to this assembly), same precedent as
    // CompiledDotnetLanguageHostBase's RsPrefix/InputRequestPrefix mirroring Invoklet.Runtime.Util's.
    internal const string CurrentQueryPathEnvVar = "Invoklet_CURRENT_QUERY_PATH";

    // Same reasoning: the script reads these, never its `args`. Invoklet_CANCEL_FILE is mirrored by
    // Invoklet.Runtime.Util.CancelFileEnvVar (see RequestStop).
    internal const string CancelFileEnvVar = "Invoklet_CANCEL_FILE";
    internal const string ParentProcessIdEnvVar = "Invoklet_PARENT_PID";
    internal const string ArgumentContractEnvVar = "Invoklet_ARGUMENT_CONTRACT";

    /// <summary>Creates a process for running a compiled .NET assembly.</summary>
    /// <param name="arguments">The script's own command-line arguments, its <c>args</c>.</param>
    public static ChildProcess ForExecution(
        ExecutionOptions options, string assemblyPath, string parentPid, string? currentQueryPath = null,
        IReadOnlyList<string>? arguments = null, string? argumentContractJson = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Runtime.RuntimePath,
            WorkingDirectory = options.WorkingDirectory,
            CreateNoWindow = !options.CreateWindow,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // ArgumentList quotes each item for the platform, so an argument with spaces or quotes
        // reaches the script unchanged. Everything after the assembly becomes the script's `args`.
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments ?? [])
            startInfo.ArgumentList.Add(argument);
        if (currentQueryPath is not null)
            startInfo.Environment[CurrentQueryPathEnvVar] = currentQueryPath;
        if (argumentContractJson is not null)
            startInfo.Environment[ArgumentContractEnvVar] = argumentContractJson;
        startInfo.Environment[ParentProcessIdEnvVar] = parentPid;
        var cancelFile = Path.Combine(Path.GetTempPath(), $"Invoklet-stop-{Guid.NewGuid():N}");
        startInfo.Environment[CancelFileEnvVar] = cancelFile;

        return new ChildProcess(startInfo) { _cancelFilePath = cancelFile };
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
