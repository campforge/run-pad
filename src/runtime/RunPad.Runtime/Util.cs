using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using DotPad.Runtime;

// ReSharper disable once CheckNamespace

/// <summary>
/// Interactive input from the output panel (TODO.md #63) — LINQPad-compatible <c>Util.ReadLine</c>/
/// <c>Util.GetPassword</c>. Blocks the calling script until the user answers, right where <c>.Dump()</c>
/// output appears. Uses the same sentinel-prefixed stdout protocol as <c>ObjectExtensions.Dump</c>
/// (see <see cref="InputRequestPrefixChar"/>, a different prefix so the two don't collide) to ask for
/// input, paired with a plain-text reply over stdin — CompiledDotnetLanguageHostBase/
/// EditorTabItemControl wire both directions up on the host side. Only meaningful under
/// <c>RunAsync</c> (the plain, non-debug run) — a debug session's stdin isn't currently piped through.
/// </summary>
public static class Util
{
    internal const char InputRequestPrefixChar = '\x1f'; // ASCII Unit Separator — distinct from ObjectExtensions.DumpLinePrefixChar

    // TODO.md #111 (bucket 1) — mirrors ChildProcess.CurrentQueryPathEnvVar (DotPad.Build.Core), a
    // separate assembly this one (netstandard2.0, referenced by the compiled SCRIPT's own project,
    // not by DotPad itself) has no reference to — duplicated literal, not shared, same precedent as
    // InputRequestPrefixChar/RsPrefix mirroring across the same process boundary.
    internal const string CurrentQueryPathEnvVar = "DOTPAD_CURRENT_QUERY_PATH";

    /// <summary>
    /// The full path to the running script's own <c>.rpad</c> file, or <see langword="null"/> if it's
    /// never been saved (an untitled tab) — LINQPad-compatible <c>Util.CurrentQueryPath</c>. Works
    /// under both a plain run and a debug session (DapSession forwards it via the launch request's
    /// own <c>env</c>, since netcoredbg spawns the debuggee itself rather than DotPad).
    /// </summary>
    public static string? CurrentQueryPath => Environment.GetEnvironmentVariable(CurrentQueryPathEnvVar);

    // TODO.md #111 (bucket 1) — LINQPad-compatible Util.SaveString/LoadString/SaveBytes/LoadBytes save
    // to a dedicated per-user data folder, not an arbitrary path the caller has to come up with —
    // mirrors the OS-special-folder + "DotPad" subfolder convention DotPad's own
    // PreferencesService/BuildCacheService already use for their own data (a further "UserData"
    // level under that so scripts' own files don't collide with either), duplicated rather than
    // shared since this assembly (netstandard2.0, referenced by the compiled SCRIPT's own project)
    // has no reference to DotPad's main app project. fileName may itself contain subdirectories
    // (e.g. "cache/foo.json") — created on demand by the Save* half, same as LINQPad's own behavior.
    private static string ResolveUserDataPath(string fileName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotPad", "UserData");
        var path = Path.Combine(root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="fileName"/> in DotPad's per-user data folder (created on demand), overwriting any existing content.</summary>
    public static void SaveString(string text, string fileName) => File.WriteAllText(ResolveUserDataPath(fileName), text);

    /// <summary>Reads back a file previously written by <see cref="SaveString"/> — throws the usual <see cref="IOException"/> family if it doesn't exist.</summary>
    public static string LoadString(string fileName) => File.ReadAllText(ResolveUserDataPath(fileName));

    /// <summary>Writes <paramref name="data"/> to <paramref name="fileName"/> in DotPad's per-user data folder (created on demand), overwriting any existing content.</summary>
    public static void SaveBytes(byte[] data, string fileName) => File.WriteAllBytes(ResolveUserDataPath(fileName), data);

    /// <summary>Reads back a file previously written by <see cref="SaveBytes"/> — throws the usual <see cref="IOException"/> family if it doesn't exist.</summary>
    public static byte[] LoadBytes(string fileName) => File.ReadAllBytes(ResolveUserDataPath(fileName));

    // TODO.md #111 (bucket 1) — LINQPad-inspired Util.Cache/CacheAsync: a persistent cache keyed by an
    // explicit string, not auto-derived from the call site (no decompiled reference for LINQPad's own
    // default-key behavior was available to check against — see this bucket's own "по построению"
    // framing — so a required, explicit key avoids guessing at magic behavior). Entries live under the
    // same UserData folder as Save*/Load* (a "Cache" subdirectory), as a small JSON envelope carrying
    // an optional expiry alongside the value, so no second side-channel file is needed for the TTL.
    // duration: null means "cache forever" (until manually cleared by deleting the file on disk).
    private sealed class CacheEntry<T>
    {
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public T? Value { get; set; }
    }

    /// <summary>
    /// Returns the cached result for <paramref name="key"/> if present and not expired; otherwise runs
    /// <paramref name="function"/>, caches the result (persisted to disk, surviving across runs), and
    /// returns it. <paramref name="duration"/> is the TTL — <see langword="null"/> caches forever.
    /// </summary>
    public static T Cache<T>(Func<T> function, string key, TimeSpan? duration = null)
    {
        if (TryReadCache<T>(key, out var cached)) return cached!;

        var value = function();
        WriteCache(key, value, duration);
        return value;
    }

    /// <summary>Same as <see cref="Cache{T}(Func{T}, string, TimeSpan?)"/>, but for an async <paramref name="function"/>.</summary>
    public static async Task<T> CacheAsync<T>(Func<Task<T>> function, string key, TimeSpan? duration = null)
    {
        if (TryReadCache<T>(key, out var cached)) return cached!;

        var value = await function().ConfigureAwait(false);
        WriteCache(key, value, duration);
        return value;
    }

    private static bool TryReadCache<T>(string key, out T? value)
    {
        var path = ResolveUserDataPath(CacheFileName(key));
        if (File.Exists(path))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CacheEntry<T>>(File.ReadAllText(path));
                if (entry is not null && (entry.ExpiresAtUtc is null || entry.ExpiresAtUtc > DateTimeOffset.UtcNow))
                {
                    value = entry.Value;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Corrupt, or the same key was previously used to cache a different, incompatible T —
                // fall through and recompute rather than throwing.
            }
        }

        value = default;
        return false;
    }

    private static void WriteCache<T>(string key, T value, TimeSpan? duration)
    {
        var entry = new CacheEntry<T> { ExpiresAtUtc = duration is { } d ? DateTimeOffset.UtcNow + d : null, Value = value };
        File.WriteAllText(ResolveUserDataPath(CacheFileName(key)), JsonSerializer.Serialize(entry));
    }

    // Sanitizes against a fixed, OS-independent character set (not Path.GetInvalidFileNameChars(),
    // which differs between Windows and Linux/macOS) so the same key maps to the same on-disk file
    // name on every platform DotPad ships for.
    private static string CacheFileName(string key)
    {
        var sanitized = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return Path.Combine("Cache", sanitized + ".json");
    }

    // TODO.md #111 (bucket 1) — LINQPad-inspired Util.Cmd/CmdCore: run an external process and stream
    // its output. DotPad.Build.Core (DotPad's own ChildProcess) already has this exact
    // concurrent-stdout/stderr-draining infrastructure for the compiled SCRIPT's own process, but this
    // assembly (netstandard2.0, referenced by the compiled script's own project) has no reference to
    // it — reimplemented here directly against System.Diagnostics.Process, same principle (draining
    // one stream while the other's OS pipe buffer fills would deadlock the child — see docs/ARCHITECTURE.md's
    // note on this same class of bug), not shared code.

    /// <summary>
    /// Runs an external process, streaming its stdout/stderr lines live to <see cref="Console.Out"/>/
    /// <see cref="Console.Error"/> as they arrive (so they show up in the output panel while the
    /// process runs, not only once it exits) — LINQPad-inspired <c>Util.Cmd</c>. Returns the exit code
    /// once the process completes.
    /// </summary>
    public static int Cmd(string executable, string? arguments = null, string? workingDirectory = null)
        => CmdCore(executable, arguments, workingDirectory, Console.WriteLine, Console.Error.WriteLine);

    /// <summary>
    /// Same as <see cref="Cmd(string, string?, string?)"/>, but captures the process's combined
    /// stdout+stderr into <paramref name="output"/> (one line per <see cref="Environment.NewLine"/>)
    /// instead of writing it live.
    /// </summary>
    public static int Cmd(string executable, string? arguments, out string output)
    {
        var lines = new List<string>();
        void OnLine(string line) { lock (lines) lines.Add(line); }

        var exitCode = CmdCore(executable, arguments, workingDirectory: null, OnLine, OnLine);
        output = string.Join(Environment.NewLine, lines);
        return exitCode;
    }

    /// <summary>
    /// The process runner underneath both <see cref="Cmd(string, string?, string?)"/> overloads — runs <paramref name="executable"/>
    /// to completion, calling <paramref name="onOutputLine"/>/<paramref name="onErrorLine"/> for each
    /// stdout/stderr line as it arrives, and returns the exit code. Exposed directly for callers who
    /// want their own line handling instead of either <c>Cmd</c> overload's behavior.
    /// </summary>
    public static int CmdCore(
        string executable, string? arguments, string? workingDirectory, Action<string> onOutputLine, Action<string> onErrorLine)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = DrainAsync(process.StandardOutput, onOutputLine);
        var errorTask = DrainAsync(process.StandardError, onErrorLine);

        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);

        return process.ExitCode;
    }

    private static async Task DrainAsync(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            onLine(line);
    }

    // TODO.md #111 (bucket 1) — LINQPad-inspired Util.KeepRunning. LINQPad has its own explicit
    // "QueryCompletionCountdown" concept to hold a query open; DotPad has no such concept (a compiled
    // script is just a normal .NET console app — it exits once its own code returns, like any other),
    // so instead of building a parallel lifetime system, this leans on the CLR's own actual behavior:
    // a process only exits once every FOREGROUND thread has finished, even after Main() itself has
    // already returned. A live KeepRunning token holds a genuine foreground Thread parked on a
    // ManualResetEventSlim; disposing the last outstanding token releases it, letting the process end
    // exactly the way it would have without ever calling KeepRunning at all.
    private static readonly object KeepRunningLock = new();
    private static int _keepRunningCount;
    private static ManualResetEventSlim? _keepRunningSignal;

    // Test-only observability (InternalsVisibleTo) — is a KeepRunning token currently outstanding.
    internal static bool IsKeepRunningActive
    {
        get { lock (KeepRunningLock) return _keepRunningCount > 0; }
    }

    /// <summary>
    /// Keeps the script's process alive until the returned token is disposed, even after the script's
    /// own code has already returned — LINQPad-inspired <c>Util.KeepRunning</c>. Useful when a script
    /// kicks off background work (a <see cref="System.Threading.Timer"/>, an event handler, a detached
    /// <see cref="Task"/>) and would otherwise exit, like any console app, as soon as its own code
    /// finishes running. Safe to call more than once — the process only becomes eligible to exit again
    /// once EVERY outstanding token has been disposed. Disposing the same token twice is a no-op.
    /// </summary>
    public static IDisposable KeepRunning()
    {
        lock (KeepRunningLock)
        {
            if (_keepRunningCount == 0)
            {
                var signal = new ManualResetEventSlim(false);
                _keepRunningSignal = signal;
                new Thread(signal.Wait) { IsBackground = false }.Start();
            }
            _keepRunningCount++;
        }

        return new KeepRunningToken();
    }

    private sealed class KeepRunningToken : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            lock (KeepRunningLock)
            {
                _keepRunningCount--;
                if (_keepRunningCount == 0)
                {
                    _keepRunningSignal!.Set();
                    _keepRunningSignal = null;
                }
            }
        }
    }

    // TODO.md #111 (bucket 1) — LINQPad-inspired Util.ProgressBar. Reuses DumpContainer's own
    // live-update mechanism (a stable data-dp-id the UI matches on to replace the block in place —
    // see DumpContainer.cs) but does NOT reuse DumpContainer itself: DumpContainer.Content goes
    // through DumpRenderer.Render(), which renders a plain string as an escaped, quoted STRING VALUE
    // (correct for regular Dump() output) rather than as raw markup — a progress bar needs actual
    // <div style="width:..."> markup, so it emits its own fixed HTML directly instead.

    /// <summary>
    /// A live-updating progress bar in the output panel — LINQPad-inspired <c>Util.ProgressBar</c>.
    /// Construct it, then call <see cref="Dump"/> once to show it; after that, setting
    /// <see cref="PercentComplete"/>/<see cref="Caption"/> (or calling <see cref="Refresh"/>)
    /// re-renders the SAME block in place instead of appending a new one each time.
    /// </summary>
    public sealed class ProgressBar
    {
        private readonly string _id = Guid.NewGuid().ToString("N");
        private int _percentComplete;
        private string? _caption;
        private bool _dumped;

        /// <summary>Creates a progress bar. Nothing is shown until <see cref="Dump"/> is called.</summary>
        public ProgressBar(int percentComplete = 0, string? caption = null)
        {
            _percentComplete = Clamp(percentComplete);
            _caption = caption;
        }

        /// <summary>0-100; out-of-range values are clamped. Setting it re-renders in place.</summary>
        public int PercentComplete
        {
            get => _percentComplete;
            set { _percentComplete = Clamp(value); Refresh(); }
        }

        /// <summary>Optional text shown above the bar. Setting it re-renders in place.</summary>
        public string? Caption
        {
            get => _caption;
            set { _caption = value; Refresh(); }
        }

        /// <summary>Renders this progress bar for the first time. Returns this instance so it can be chained like <c>new Util.ProgressBar().Dump()</c>.</summary>
        public ProgressBar Dump()
        {
            _dumped = true;
            Emit();
            return this;
        }

        /// <summary>Re-renders in place. A no-op if <see cref="Dump"/> hasn't been called yet.</summary>
        public void Refresh()
        {
            if (_dumped) Emit();
        }

        private static int Clamp(int value) => value < 0 ? 0 : value > 100 ? 100 : value;

        private void Emit()
        {
            var captionHtml = _caption is null
                ? ""
                : $"<div class=\"dp-progressbar-caption\">{DumpRenderer.Encode(_caption)}</div>";
            var html = $"<div data-dp-id=\"{_id}\">{captionHtml}" +
                       $"<div class=\"dp-progressbar\" data-dp-percent=\"{_percentComplete}\">" +
                       $"<div class=\"dp-progressbar-fill\" style=\"width:{_percentComplete}%\"></div>" +
                       "</div></div>";
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
            Console.WriteLine(ObjectExtensions.DumpLinePrefixChar + encoded);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/>, returning any exception it throws instead of letting it
    /// propagate (<see langword="null"/> if it completed without throwing) — LINQPad-inspired
    /// <c>Util.Try</c>. No decompiled reference for this one's exact original signature was available
    /// to check against (see TODO.md #111's own note on this bucket being "по построению", not a
    /// byte-for-byte port); this is a straightforward, self-contained try/catch wrapper only.
    /// </summary>
    public static Exception? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Same as <see cref="Try(Action)"/>, but for a function that returns a value — <c>Result</c> is <see langword="default"/> if <paramref name="func"/> threw.</summary>
    public static (T? Result, Exception? Exception) Try<T>(Func<T> func)
    {
        try
        {
            return (func(), null);
        }
        catch (Exception ex)
        {
            return (default, ex);
        }
    }

    /// <summary>Reads a single line of text typed into the output panel. <see langword="null"/> if the run was cancelled/stdin closed before an answer arrived.</summary>
    public static string? ReadLine(string? prompt = null) => ReadLineCore(prompt, isPassword: false);

    /// <summary>Same as <see cref="ReadLine(string?)"/>, but the output panel masks what's typed — still plain text on the wire, just not shown on screen.</summary>
    public static string? GetPassword(string? prompt = null) => ReadLineCore(prompt, isPassword: true);

    /// <summary>
    /// Reads a line and converts it to <typeparamref name="T"/> via <see cref="Convert.ChangeType(object, Type)"/>
    /// (works for <see cref="int"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="DateTime"/>,
    /// any other <see cref="IConvertible"/> target type). <see langword="default"/> if the run was
    /// cancelled/stdin closed before an answer arrived.
    /// </summary>
    public static T? ReadLine<T>(string? prompt = null)
    {
        var text = ReadLineCore(prompt, isPassword: false);
        return text is null ? default : (T)Convert.ChangeType(text, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string? ReadLineCore(string? prompt, bool isPassword)
    {
        var json = JsonSerializer.Serialize(new { Prompt = prompt, IsPassword = isPassword });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Console.WriteLine(InputRequestPrefixChar + encoded);
        return Console.In.ReadLine();
    }
}
