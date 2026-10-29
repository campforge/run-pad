using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Invoklet.Runtime;

// ReSharper disable once CheckNamespace

/// <summary>
/// Interactive input from the output panel — LINQPad-compatible <c>Util.ReadLine</c>, and the masked
/// question <c>Util.GetSecret</c> asks for a secret the store doesn't have. Blocks the calling script until the user answers, right where <c>.Dump()</c>
/// output appears. Uses the same sentinel-prefixed stdout protocol as <c>ObjectExtensions.Dump</c>
/// (see <see cref="InputRequestPrefixChar"/>, a different prefix so the two don't collide) to ask for
/// input, paired with a plain-text reply over stdin — CompiledDotnetLanguageHostBase/
/// EditorTabItemControl wire both directions up on the host side. Only meaningful under
/// <c>RunAsync</c> (the plain, non-debug run) — a debug session's stdin isn't currently piped through.
/// </summary>
public static class Util
{
    internal const char InputRequestPrefixChar = '\x1f'; // ASCII Unit Separator — distinct from ObjectExtensions.DumpLinePrefixChar

    // Mirrors ChildProcess.CurrentQueryPathEnvVar (Invoklet.Build.Core), a
    // separate assembly this one (netstandard2.0, referenced by the compiled SCRIPT's own project,
    // not by Invoklet itself) has no reference to — duplicated literal, not shared, same precedent as
    // InputRequestPrefixChar/RsPrefix mirroring across the same process boundary.
    internal const string CurrentQueryPathEnvVar = "INVOKLET_CURRENT_QUERY_PATH";

    /// <summary>
    /// The full path to the running script's own <c>.inv</c> file, or <see langword="null"/> if it's
    /// never been saved (an untitled tab) — LINQPad-compatible <c>Util.CurrentQueryPath</c>. Works
    /// under both a plain run and a debug session (DapSession forwards it via the launch request's
    /// own <c>env</c>, since netcoredbg spawns the debuggee itself rather than Invoklet).
    /// </summary>
    public static string? CurrentQueryPath => Environment.GetEnvironmentVariable(CurrentQueryPathEnvVar);

    // Mirror ChildProcess.CancelFileEnvVar/CompiledDotnetLanguageHostBase.CancelListeningPrefix: Invoklet
    // creates this file when the user presses Stop, and learns that the script listens for it from a
    // stdout line starting with CancelListeningPrefixChar.
    internal const string CancelFileEnvVar = "INVOKLET_CANCEL_FILE";
    internal const char CancelListeningPrefixChar = '\x1d'; // ASCII Group Separator
    private static readonly TimeSpan CancelPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly object CancellationLock = new();
    private static CancellationTokenSource? _cancellation;
    private static Timer? _cancelPoll;

    /// <summary>
    /// Cancelled when the user presses Stop. A script that uses it — directly, or by declaring
    /// <c>Main(CancellationToken)</c> / <c>Main(string[], CancellationToken)</c> — gets a few seconds to
    /// close files, connections and child processes before Invoklet ends it; a script that never asks
    /// for it is stopped at once. Never cancelled when the script runs outside Invoklet.
    /// </summary>
    public static CancellationToken CancellationToken
    {
        get
        {
            lock (CancellationLock)
            {
                if (_cancellation is not null) return _cancellation.Token;
                _cancellation = new CancellationTokenSource();
                if (Environment.GetEnvironmentVariable(CancelFileEnvVar) is { Length: > 0 } cancelFile)
                {
                    var cts = _cancellation;
                    _cancelPoll = new Timer(_ =>
                    {
                        if (!File.Exists(cancelFile)) return;
                        _cancelPoll?.Dispose();
                        cts.Cancel();
                    }, null, CancelPollInterval, CancelPollInterval);
                    Console.WriteLine(CancelListeningPrefixChar.ToString());
                    Console.Out.Flush();
                }
                return _cancellation.Token;
            }
        }
    }

    /// <summary>
    /// The process-wide default rendering options every <c>.Dump()</c> call starts from —
    /// LINQPad-compatible <c>Util.DumpDefaults</c> (signature verified against the real
    /// <c>LINQPad.Runtime.dll</c>). Same instance as <see cref="DumpOptions.Default"/>; mutate its
    /// properties directly (e.g. <c>Util.DumpDefaults.MaxDepth = 5;</c>) to change the default for
    /// the rest of the run instead of passing a <see cref="DumpOptions"/> to every individual call.
    /// </summary>
    public static DumpOptionsCore DumpDefaults => DumpOptions.GlobalDefaults;

    // LINQPad-compatible Util.SaveString/LoadString/SaveBytes/LoadBytes save
    // to a dedicated per-user data folder, not an arbitrary path the caller has to come up with —
    // mirrors the OS-special-folder + "Invoklet" subfolder convention Invoklet's own
    // PreferencesService/BuildCacheService already use for their own data (a further "UserData"
    // level under that so scripts' own files don't collide with either), duplicated rather than
    // shared since this assembly (netstandard2.0, referenced by the compiled SCRIPT's own project)
    // has no reference to Invoklet's main app project. fileName may itself contain subdirectories
    // (e.g. "cache/foo.json") — created on demand by the Save* half, same as LINQPad's own behavior.
    private static string ResolveUserDataPath(string fileName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "UserData");
        var path = Path.Combine(root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="fileName"/> in Invoklet's per-user data folder (created on demand), overwriting any existing content.</summary>
    public static void SaveString(string text, string fileName) => File.WriteAllText(ResolveUserDataPath(fileName), text);

    /// <summary>Reads back a file previously written by <see cref="SaveString"/> — throws the usual <see cref="IOException"/> family if it doesn't exist.</summary>
    public static string LoadString(string fileName) => File.ReadAllText(ResolveUserDataPath(fileName));

    /// <summary>Writes <paramref name="data"/> to <paramref name="fileName"/> in Invoklet's per-user data folder (created on demand), overwriting any existing content.</summary>
    public static void SaveBytes(byte[] data, string fileName) => File.WriteAllBytes(ResolveUserDataPath(fileName), data);

    /// <summary>Reads back a file previously written by <see cref="SaveBytes"/> — throws the usual <see cref="IOException"/> family if it doesn't exist.</summary>
    public static byte[] LoadBytes(string fileName) => File.ReadAllBytes(ResolveUserDataPath(fileName));

    // LINQPad-inspired Util.Cache/CacheAsync: a persistent cache keyed by an
    // explicit string, not auto-derived from the call site (no decompiled reference for LINQPad's own
    // default-key behavior was available to check against — see this bucket's own "by construction"
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
                var entry = (CacheEntry<T>?)ScriptJson.Deserialize(File.ReadAllText(path), typeof(CacheEntry<T>));
                if (entry is not null && (entry.ExpiresAtUtc is null || entry.ExpiresAtUtc > DateTimeOffset.UtcNow))
                {
                    value = entry.Value;
                    return true;
                }
            }
            catch (Exception ex) when (ScriptJson.IsJsonException(ex))
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
        File.WriteAllText(ResolveUserDataPath(CacheFileName(key)), ScriptJson.Serialize(entry, typeof(CacheEntry<T>)));
    }

    // Sanitizes against a fixed, OS-independent character set (not Path.GetInvalidFileNameChars(),
    // which differs between Windows and Linux/macOS) so the same key maps to the same on-disk file
    // name on every platform Invoklet ships for.
    private static string CacheFileName(string key)
    {
        var sanitized = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return Path.Combine("Cache", sanitized + ".json");
    }

    // LINQPad-inspired Util.Cmd/CmdCore: run an external process and stream
    // its output. Invoklet.Build.Core (Invoklet's own ChildProcess) already has this exact
    // concurrent-stdout/stderr-draining infrastructure for the compiled SCRIPT's own process, but this
    // assembly (netstandard2.0, referenced by the compiled script's own project) has no reference to
    // it — reimplemented here directly against System.Diagnostics.Process, same principle (draining
    // one stream while the other's OS pipe buffer fills would deadlock the child, the same
    // class of bug the language hosts guard against), not shared code.

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

    // LINQPad-inspired Util.KeepRunning. LINQPad has its own explicit
    // "QueryCompletionCountdown" concept to hold a query open; Invoklet has no such concept (a compiled
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

    // LINQPad-inspired Util.ProgressBar. Reuses DumpContainer's own
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
    /// to check against (written by construction, not a
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

    // Mirrors FileSecretStore.FileEnvVar and its file format (Invoklet's main app, which writes the
    // store), duplicated rather than shared for the same reason as the data folder above.
    internal const string SecretsFileEnvVar = "INVOKLET_SECRETS_FILE";

    // Set to 1 by Invoklet for a run whose input requests it answers (not under the debugger, nor the
    // command line), mirroring ChildProcess.InputEnvVar.
    internal const string InputEnvVar = "INVOKLET_INPUT";

    // Secrets this run asked the user for: the store may have been encrypted by that very answer,
    // with a key this process never got.
    private static readonly Dictionary<string, string> AskedSecrets = new(StringComparer.OrdinalIgnoreCase);

    private sealed class SecretStoreFile
    {
        public int Version { get; set; }
        public string? Protection { get; set; }
        public Dictionary<string, string>? Secrets { get; set; }
    }

    /// <summary>
    /// The secret named <paramref name="name"/> (letter case aside) from Invoklet's Secret Store
    /// (Tools → Secret Store), so a password, token or key stays out of the script and its file.
    /// The store belongs to the user on this machine: on Windows it is encrypted for that user, on
    /// macOS and Linux with a key kept in the system keychain, which Invoklet passes to the script.
    /// A secret the store doesn't have yet is asked for, masked, as LINQPad's <c>Util.GetPassword</c>
    /// does, and Invoklet keeps the answer in the store for next time.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The store has no such secret, and it couldn't be asked
    /// for (under the debugger or the command line) or the run was stopped instead.</exception>
    /// <exception cref="InvalidDataException">The store can't be read here.</exception>
    /// <exception cref="InvalidOperationException">The store is encrypted with a keychain key and the
    /// script wasn't started by Invoklet or its command line, which pass that key.</exception>
    public static string GetSecret(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        var path = Environment.GetEnvironmentVariable(SecretsFileEnvVar) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "secrets.json");
        var missing = new KeyNotFoundException(
            $"Invoklet's Secret Store has no secret named '{name}'. Add it in Tools → Secret Store ({path}).");
        lock (AskedSecrets)
        {
            if (AskedSecrets.TryGetValue(name, out var asked)) return asked;
        }
        if (!File.Exists(path)) return AskForSecret(name) ?? throw missing;

        SecretStoreFile? store;
        try
        {
            store = (SecretStoreFile?)ScriptJson.Deserialize(File.ReadAllText(path), typeof(SecretStoreFile));
        }
        catch (Exception ex) when (ScriptJson.IsJsonException(ex))
        {
            throw new InvalidDataException($"Invoklet's Secret Store ({path}) is damaged.", ex);
        }
        if (store is null || store.Version != 1)
            throw new InvalidDataException($"Invoklet's Secret Store ({path}) was written by a newer Invoklet.");

        var entry = store.Secrets?.FirstOrDefault(s => string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase)) ?? default;
        var value = entry.Value;
        if (value is null) return AskForSecret(name) ?? throw missing;
        switch (store.Protection)
        {
            case "None":
                return value;
            case "DPAPI" when System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows):
                try
                {
                    return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(value)));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FormatException)
                {
                    throw new InvalidDataException($"Invoklet's Secret Store ({path}) is encrypted for another Windows user or is damaged.", ex);
                }
            case "DPAPI":
                throw new InvalidDataException($"Invoklet's Secret Store ({path}) is encrypted for a Windows user and can't be read here.");
            case SecretCipher.Protection:
                if (Environment.GetEnvironmentVariable(SecretCipher.KeyEnvVar) is not { Length: > 0 } key)
                    throw new InvalidOperationException(
                        $"Invoklet's Secret Store ({path}) is encrypted with a key kept in the system keychain: run the script from Invoklet or the invoklet command line, which pass that key.");
                try
                {
                    // The stored name, not the one asked for: the value is bound to it letter for letter.
                    return SecretCipher.Decrypt(Convert.FromBase64String(key), entry.Key, value);
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
                {
                    throw new InvalidDataException($"Invoklet's Secret Store ({path}) doesn't match the key Invoklet passed, or is damaged.", ex);
                }
            default:
                throw new InvalidDataException($"Invoklet's Secret Store ({path}) uses an unknown protection ({store.Protection}).");
        }
    }

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

    /// <summary>
    /// Saves a secret in Invoklet's Secret Store, or replaces the value of the one of that name
    /// (letter case aside), as LINQPad's <c>Util.SetPassword</c> does. Invoklet writes it, so it
    /// works only in a script Invoklet runs (not under the debugger or the command line).
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing here can save it (debugger, command line).</exception>
    /// <exception cref="IOException">Invoklet couldn't write its Secret Store.</exception>
    public static void SetSecret(string name, string value)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (name.Trim().Length == 0) throw new ArgumentException("A secret needs a name.", nameof(name));
        if (Environment.GetEnvironmentVariable(InputEnvVar) != "1")
            throw new InvalidOperationException(
                "Util.SetSecret saves through Invoklet: run the script from Invoklet, not under the debugger or the command line.");
        // An input request with the value: Invoklet keeps it and answers 1 (kept) or 0, asking nobody.
        var json = "{\"Secret\":" + JsonString(name) + ",\"Value\":" + JsonString(value) + "}";
        Console.WriteLine(InputRequestPrefixChar + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        if (Console.In.ReadLine() != "1")
            throw new IOException($"Invoklet couldn't save the secret '{name}' in its Secret Store.");
        lock (AskedSecrets) AskedSecrets[name] = value;
    }

    // Only where Invoklet answers input; null when it can't be asked, or the run ended instead.
    private static string? AskForSecret(string name)
    {
        if (Environment.GetEnvironmentVariable(InputEnvVar) != "1") return null;
        var value = ReadLineCore(
            $"The Secret Store has no secret named '{name}'. Enter it; Invoklet keeps it there for next time:",
            isPassword: true, secret: name);
        if (value is null) return null;
        lock (AskedSecrets) AskedSecrets[name] = value;
        return value;
    }

    private static string? ReadLineCore(string? prompt, bool isPassword, string? secret = null)
    {
        // Written by hand: the host (CompiledDotnetLanguageHostBase.ParseInputRequest) reads exactly these
        // properties, and this is the only JSON the runtime writes outside Util.Cache. Secret names the
        // secret Util.GetSecret asks for: the host keeps the answer in the Secret Store.
        var json = "{\"Prompt\":" + JsonString(prompt) + ",\"IsPassword\":" + (isPassword ? "true" : "false")
            + (secret is null ? "" : ",\"Secret\":" + JsonString(secret)) + "}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Console.WriteLine(InputRequestPrefixChar + encoded);
        return Console.In.ReadLine();
    }

    // A JSON string literal (or null), escaped per RFC 8259.
    private static string JsonString(string? value)
    {
        if (value is null) return "null";
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // The script's own System.Text.Json, reached by reflection, so this netstandard2.0 library needs no
    // System.Text.Json package (scripts reference it as a bare DLL, so the package never reached
    // them). A compile-time reference to STJ 8.0 made Util.Cache throw FileLoadException/
    // FileNotFoundException on net6/net7 scripts, whose in-box STJ is older (verified against both).
    // Every .NET a script can run on (net6+) ships STJ in the box; binding at runtime uses whichever
    // version is there. Util.Cache needs a real serializer (arbitrary T); Util.GetSecret reads Invoklet's store with it.
    private static class ScriptJson
    {
        private static readonly Lazy<(MethodInfo Serialize, MethodInfo Deserialize, Type JsonException)> Api = new(Load);

        private static (MethodInfo, MethodInfo, Type) Load()
        {
            var serializer = Type.GetType("System.Text.Json.JsonSerializer, System.Text.Json", throwOnError: false)
                ?? throw new PlatformNotSupportedException("Util.Cache and Util.GetSecret need System.Text.Json, which ships with .NET 6 and later.");
            var assembly = serializer.Assembly;
            var options = assembly.GetType("System.Text.Json.JsonSerializerOptions", throwOnError: true)!;
            return (
                serializer.GetMethod("Serialize", [typeof(object), typeof(Type), options])!,
                serializer.GetMethod("Deserialize", [typeof(string), typeof(Type), options])!,
                assembly.GetType("System.Text.Json.JsonException", throwOnError: true)!);
        }

        public static string Serialize(object? value, Type type) => (string)Invoke(Api.Value.Serialize, value, type)!;

        public static object? Deserialize(string json, Type type) => Invoke(Api.Value.Deserialize, json, type);

        public static bool IsJsonException(Exception ex) => Api.Value.JsonException.IsInstanceOfType(ex);

        // Default options (null), and the serializer's own exception rather than the reflection wrapper.
        private static object? Invoke(MethodInfo method, object? value, Type type)
        {
            try
            {
                return method.Invoke(null, [value, type, null]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
