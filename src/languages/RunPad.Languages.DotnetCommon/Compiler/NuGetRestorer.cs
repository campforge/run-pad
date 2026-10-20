using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NuGet.Client;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace RunPad.Languages.DotnetCommon.Compiler;

// What a script's packages contribute to its build, from the restore's lock file (project.assets.json):
// assemblies to compile against, files to place next to the script (managed runtime assets, the current
// platform's runtime/native assets, satellite resources), and source generator assemblies.
public sealed record RestoredPackages(
    IReadOnlyList<string> CompileReferences,
    IReadOnlyList<RuntimeFile> RuntimeFiles,
    IReadOnlyList<string> GeneratorAssemblies,
    IReadOnlyList<ResultItem> Errors,
    IReadOnlyList<ResultItem> Warnings)
{
    public static RestoredPackages Empty { get; } = new([], [], [], [], []);
}

/// <param name="RelativePath">Destination under the output's bin directory (e.g. "de/Lib.resources.dll").</param>
public sealed record RuntimeFile(string SourcePath, string RelativePath);

// In-process `dotnet restore` for a script: NuGet's own
// RestoreRunner over a PackageSpec built from the front matter, so version resolution (latest/"*",
// ranges, transitive dependencies), the global packages folder, sources and credentials behave like the
// SDK's — without MSBuild. Restore only runs when the package set, target or NuGet configuration
// changed; otherwise the previous lock file is read back.
public static class NuGetRestorer
{
    private const string ProjectName = "RunPadQuery";

    public static async Task<RestoredPackages> RestoreAsync(
        IReadOnlyDictionary<string, string> packages, string targetFramework, string languageFolder,
        string objDirectory, string? nugetConfigFile, CancellationToken ct)
    {
        if (packages.Count == 0) return RestoredPackages.Empty;

        var invalid = packages.Where(p => !IsValidVersion(p.Value))
            .Select(p => Message("NU1008", $"'{p.Value}' is not a valid version string for package {p.Key}."))
            .ToList();
        if (invalid.Count > 0)
            return new RestoredPackages([], [], [], invalid, []);

        Directory.CreateDirectory(objDirectory);
        var settings = LoadSettings(nugetConfigFile, objDirectory);
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        var framework = NuGetFramework.Parse(targetFramework);
        var assetsPath = Path.Combine(objDirectory, LockFileFormat.AssetsFileName);
        var keyPath = Path.Combine(objDirectory, ProjectName + ".restore.key");
        var key = RestoreKey(packages, targetFramework, nugetConfigFile);

        var errors = new List<ResultItem>();
        var warnings = new List<ResultItem>();
        if (!(File.Exists(keyPath) && File.Exists(assetsPath) && await File.ReadAllTextAsync(keyPath, ct) == key))
        {
            var logger = new CollectingLogger();
            var restored = await RunRestoreAsync(packages, framework, targetFramework, objDirectory, settings,
                globalPackagesFolder, logger, ct);
            errors.AddRange(logger.Errors);
            warnings.AddRange(logger.Warnings);
            if (!restored || errors.Count > 0)
            {
                File.Delete(keyPath);
                if (errors.Count == 0)
                    errors.Add(Message("NU1000", "Restoring the script's NuGet packages failed."));
                return new RestoredPackages([], [], [], errors, warnings);
            }
            await File.WriteAllTextAsync(keyPath, key, ct);
        }

        var lockFile = LockFileUtilities.GetLockFile(assetsPath, NullLogger.Instance);
        var target = lockFile?.GetTarget(framework, runtimeIdentifier: null);
        if (lockFile is null || target is null)
            return new RestoredPackages([], [], [], [Message("NU1000", "The package restore produced no assets for " + targetFramework + ".")], warnings);

        var folders = new[] { globalPackagesFolder }.Concat(SettingsUtility.GetFallbackPackageFolders(settings)).ToList();
        return SelectAssets(lockFile, target, folders, languageFolder, warnings);
    }

    // ── Without restoring ────────────────────────────────────────────────────

    /// <summary>
    /// The assemblies to compile against for <paramref name="packages"/>, for an editor that must not
    /// restore (no network on every keystroke). When the last restore in <paramref name="objDirectory"/>
    /// was made for exactly these packages, target framework and NuGet configuration, its selection is
    /// used — the versions it resolved, transitive packages and the framework's compile assets, the
    /// same files a build compiles against. Otherwise each package is looked up in the local package
    /// folders with NuGet's own version rules (<see cref="ResolveFromPackageFolders"/>).
    /// </summary>
    public static IReadOnlyList<string> CompileReferencesWithoutRestore(
        IReadOnlyDictionary<string, string> packages, string targetFramework, string? objDirectory,
        string? nugetConfigFile)
    {
        if (packages.Count == 0) return [];
        try
        {
            if (objDirectory is not null
                && ReadRestored(packages, targetFramework, objDirectory, nugetConfigFile) is { } restored)
                return restored;

            var settings = LoadSettings(nugetConfigFile, objDirectory ?? Path.GetTempPath());
            var folders = new[] { SettingsUtility.GetGlobalPackagesFolder(settings) }
                .Concat(SettingsUtility.GetFallbackPackageFolders(settings)).ToList();
            return ResolveFromPackageFolders(packages, targetFramework, folders);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or FormatException or ArgumentException or InvalidOperationException)
        {
            // An unreadable configuration or lock file: no package types rather than a broken editor.
            return [];
        }
    }

    /// <summary>The compile references of the restore in <paramref name="objDirectory"/>, or null when it
    /// is missing or was made for other packages, another target framework or another configuration.</summary>
    internal static IReadOnlyList<string>? ReadRestored(
        IReadOnlyDictionary<string, string> packages, string targetFramework, string objDirectory, string? nugetConfigFile)
    {
        var assetsPath = Path.Combine(objDirectory, LockFileFormat.AssetsFileName);
        var keyPath = Path.Combine(objDirectory, ProjectName + ".restore.key");
        if (!File.Exists(keyPath) || !File.Exists(assetsPath)
            || File.ReadAllText(keyPath) != RestoreKey(packages, targetFramework, nugetConfigFile))
            return null;

        var lockFile = LockFileUtilities.GetLockFile(assetsPath, NullLogger.Instance);
        var target = lockFile?.GetTarget(NuGetFramework.Parse(targetFramework), runtimeIdentifier: null);
        if (lockFile is null || target is null) return null;

        var settings = LoadSettings(nugetConfigFile, objDirectory);
        var folders = new[] { SettingsUtility.GetGlobalPackagesFolder(settings) }
            .Concat(SettingsUtility.GetFallbackPackageFolders(settings)).ToList();
        // The language folder only matters for source generators, which are not compile references.
        return SelectAssets(lockFile, target, folders, languageFolder: "cs", []).CompileReferences;
    }

    /// <summary>
    /// Before any restore: for each package, the version the restore would choose among those already
    /// extracted in <paramref name="packageFolders"/> — <see cref="VersionRange.FindBestMatch"/> with the
    /// version text parsed as restore parses it ("latest" and "*" float to the newest stable version, a
    /// plain version is a minimum and prefers itself, ranges keep their exclusive bounds, prerelease
    /// versions only when the range asks for them) — and that version's compile assets for
    /// <paramref name="targetFramework"/> (ref/ before lib/, nearest framework), chosen by NuGet's own
    /// conventions. Only the listed packages: their dependencies come with the first restore. A package
    /// with an invalid version, or with no version in the folders that satisfies it, contributes nothing.
    /// </summary>
    internal static IReadOnlyList<string> ResolveFromPackageFolders(
        IReadOnlyDictionary<string, string> packages, string targetFramework, IReadOnlyList<string> packageFolders)
    {
        var framework = NuGetFramework.Parse(targetFramework);
        var conventions = new ManagedCodeConventions(new NuGet.RuntimeModel.RuntimeGraph());
        var result = new List<string>();

        foreach (var (id, versionText) in packages)
        {
            if (!IsValidVersion(versionText)) continue;
            var range = ParseVersion(versionText);

            var candidates = packageFolders
                .Select(folder => Path.Combine(folder, id.ToLowerInvariant()))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(dir => (Dir: dir, Version: NuGetVersion.TryParse(Path.GetFileName(dir), out var v) ? v : null))
                // NuGet writes .nupkg.metadata last when it extracts a package: without it the folder is
                // incomplete (an extraction in progress or interrupted).
                .Where(c => c.Version is not null && File.Exists(Path.Combine(c.Dir, ".nupkg.metadata")))
                .ToList();
            var best = range.FindBestMatch(candidates.Select(c => c.Version!));
            if (best is null) continue;
            var packageDir = candidates.First(c => c.Version == best).Dir;

            var files = Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(packageDir, f).Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
            var items = new ContentItemCollection();
            items.Load(files);
            var group = items.FindBestItemGroup(conventions.Criteria.ForFramework(framework),
                conventions.Patterns.CompileRefAssemblies, conventions.Patterns.CompileLibAssemblies);
            if (group is null) continue;

            result.AddRange(group.Items
                .Where(i => i.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(i => Path.Combine(packageDir, i.Path.Replace('/', Path.DirectorySeparatorChar))));
        }

        return result;
    }

    // Downloads the targeting pack for a target framework (highest patch of its major.minor) into the
    // global packages folder — what the SDK does when no matching pack is installed.
    public static async Task<string?> DownloadTargetingPackAsync(string targetFramework, string? nugetConfigFile,
        string objDirectory, CancellationToken ct)
    {
        if (!TargetFramework.TryParse(targetFramework, out var major, out var minor)) return null;
        var settings = LoadSettings(nugetConfigFile, objDirectory);
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        using var cache = new SourceCacheContext();

        foreach (var source in new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled))
        {
            try
            {
                var repository = Repository.Factory.GetCoreV3(source);
                var finder = await repository.GetResourceAsync<FindPackageByIdResource>(ct);
                if (finder is null) continue;
                var versions = await finder.GetAllVersionsAsync(TargetingPack.PackageId, cache, NullLogger.Instance, ct);
                var best = versions.Where(v => v.Major == major && v.Minor == minor && !v.IsPrerelease).Max();
                if (best is null) continue;

                var identity = new PackageIdentity(TargetingPack.PackageId, best);
                var downloader = await finder.GetPackageDownloaderAsync(identity, cache, NullLogger.Instance, ct);
                if (downloader is null) continue;
                using var result = await GlobalPackagesFolderUtility.AddPackageAsync(
                    source.Source, identity, await CopyToMemoryAsync(downloader, ct), globalPackagesFolder,
                    Guid.Empty, NuGet.Packaging.Signing.ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance),
                    NullLogger.Instance, ct);
                if (result.Status == DownloadResourceResultStatus.Available) return globalPackagesFolder;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try the next source.
            }
        }
        return null;
    }

    // The folder of one package version in the global packages folder, downloaded first when it isn't
    // there: build tools RunPad runs itself (protoc from Grpc.Tools), not references of a script.
    public static async Task<string> GetPackageFolderAsync(string id, string version, string? nugetConfigFile,
        CancellationToken ct)
    {
        // The settings are read from RunPad's own folder (and the user's NuGet settings), never from
        // a folder other users can write to such as the temp folder: what this returns is executed.
        var settings = LoadSettings(nugetConfigFile, RunPadNuGetConfigStore.DefaultDirectory);
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        var identity = new PackageIdentity(id, NuGetVersion.Parse(version));
        var folder = new VersionFolderPathResolver(globalPackagesFolder).GetInstallPath(identity.Id, identity.Version);
        if (File.Exists(new VersionFolderPathResolver(globalPackagesFolder).GetHashPath(identity.Id, identity.Version)))
            return folder;

        using var cache = new SourceCacheContext();
        Exception? last = null;
        foreach (var source in new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled))
        {
            try
            {
                var repository = Repository.Factory.GetCoreV3(source);
                var finder = await repository.GetResourceAsync<FindPackageByIdResource>(ct);
                if (finder is null) continue;
                var downloader = await finder.GetPackageDownloaderAsync(identity, cache, NullLogger.Instance, ct);
                if (downloader is null) continue;
                using var result = await GlobalPackagesFolderUtility.AddPackageAsync(
                    source.Source, identity, await CopyToMemoryAsync(downloader, ct), globalPackagesFolder,
                    Guid.Empty, NuGet.Packaging.Signing.ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance),
                    NullLogger.Instance, ct);
                if (result.Status == DownloadResourceResultStatus.Available) return folder;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex; // Try the next source.
            }
        }
        throw new IOException($"The NuGet package {id} {version} couldn't be downloaded" + (last is null ? "." : $": {last.Message}"), last);
    }

    public static ISettings LoadSettings(string? nugetConfigFile, string root) =>
        nugetConfigFile is not null && File.Exists(nugetConfigFile)
            ? Settings.LoadSpecificSettings(Path.GetDirectoryName(nugetConfigFile)!, Path.GetFileName(nugetConfigFile))
            : Settings.LoadDefaultSettings(root);

    // ── Restore ──────────────────────────────────────────────────────────────

    private static async Task<bool> RunRestoreAsync(
        IReadOnlyDictionary<string, string> packages, NuGetFramework framework, string targetFramework,
        string objDirectory, ISettings settings, string globalPackagesFolder, ILogger logger, CancellationToken ct)
    {
        var projectPath = Path.Combine(Path.GetDirectoryName(objDirectory)!, ProjectName + ".nugetproj");
        var sources = new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();

        var dependencies = packages
            .Select(p => new LibraryDependency
            {
                LibraryRange = new LibraryRange(p.Key, ParseVersion(p.Value), LibraryDependencyTarget.Package),
            })
            .ToImmutableArray();

        var spec = new PackageSpec([new TargetFrameworkInformation
        {
            FrameworkName = framework,
            TargetAlias = targetFramework,
            Dependencies = dependencies,
        }])
        {
            Name = ProjectName,
            FilePath = projectPath,
            RestoreMetadata = new ProjectRestoreMetadata
            {
                ProjectStyle = ProjectStyle.PackageReference,
                ProjectName = ProjectName,
                ProjectPath = projectPath,
                ProjectUniqueName = projectPath,
                OutputPath = objDirectory,
                PackagesPath = globalPackagesFolder,
                CacheFilePath = NoOpRestoreUtilities.GetProjectCacheFilePath(objDirectory),
                OriginalTargetFrameworks = [targetFramework],
                Sources = sources,
                FallbackFolders = [.. SettingsUtility.GetFallbackPackageFolders(settings)],
                ConfigFilePaths = [.. settings.GetConfigFilePaths()],
                TargetFrameworks = [new ProjectRestoreMetadataFrameworkInfo(framework) { TargetAlias = targetFramework }],
            },
        };

        var graph = new DependencyGraphSpec();
        graph.AddProject(spec);
        graph.AddRestore(spec.RestoreMetadata.ProjectUniqueName);

        using var cache = new SourceCacheContext();
        var args = new RestoreArgs
        {
            CacheContext = cache,
            Log = logger,
            GlobalPackagesFolder = globalPackagesFolder,
            CachingSourceProvider = new CachingSourceProvider(new PackageSourceProvider(settings)),
            PreLoadedRequestProviders = [new DependencyGraphSpecRequestProvider(new RestoreCommandProvidersCache(), graph)],
            AllowNoOp = true,
        };

        var summaries = await RestoreRunner.RunAsync(args, ct);
        return summaries.All(s => s.Success);
    }

    // "latest" is RunPad's word for NuGet's floating "*" (newest stable), like MsBuildProcessCompiler did.
    private static VersionRange ParseVersion(string version) =>
        string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(version)
            ? VersionRange.Parse("*")
            : VersionRange.Parse(version);

    private static bool IsValidVersion(string version) =>
        string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(version)
        || VersionRange.TryParse(version, out _);

    private static string RestoreKey(IReadOnlyDictionary<string, string> packages, string tfm, string? configFile)
    {
        var builder = new StringBuilder(tfm).Append('\n');
        foreach (var (id, version) in packages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(id.ToLowerInvariant()).Append('=').Append(version).Append('\n');
        if (configFile is not null && File.Exists(configFile))
            builder.Append(File.ReadAllText(configFile));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    // ── Asset selection ──────────────────────────────────────────────────────

    private static RestoredPackages SelectAssets(LockFile lockFile, LockFileTarget target,
        IReadOnlyList<string> packageFolders, string languageFolder, List<ResultItem> warnings)
    {
        var compile = new List<string>();
        var runtime = new Dictionary<string, RuntimeFile>(StringComparer.OrdinalIgnoreCase);
        var generators = new List<string>();
        var rids = RuntimeIdentifierFallbacks();

        foreach (var library in target.Libraries.Where(l => l.Type == LibraryType.Package))
        {
            var packageDir = FindPackageDirectory(lockFile, library, packageFolders);
            if (packageDir is null)
            {
                warnings.Add(Message("NU1101", $"Package {library.Name} {library.Version} was restored but isn't in any package folder."));
                continue;
            }

            string Full(string relative) => Path.Combine(packageDir, relative.Replace('/', Path.DirectorySeparatorChar));

            compile.AddRange(library.CompileTimeAssemblies.Where(IsRealAsset).Select(a => Full(a.Path)));
            foreach (var asset in library.RuntimeAssemblies.Concat(library.NativeLibraries).Where(IsRealAsset))
                runtime[Path.GetFileName(asset.Path)] = new RuntimeFile(Full(asset.Path), Path.GetFileName(asset.Path));
            foreach (var asset in library.ResourceAssemblies.Where(IsRealAsset))
            {
                var culture = asset.Properties.TryGetValue("locale", out var locale) ? locale : "";
                var relative = Path.Combine(culture, Path.GetFileName(asset.Path));
                runtime[relative] = new RuntimeFile(Full(asset.Path), relative);
            }

            // runtimes/<rid>/{lib,native}: the most specific RID of the current platform wins and
            // replaces the portable asset of the same name (what a RID-specific publish does).
            foreach (var group in library.RuntimeTargets.Where(IsRealAsset)
                         .Where(a => rids.Contains(a.Runtime))
                         .GroupBy(a => Path.GetFileName(a.Path), StringComparer.OrdinalIgnoreCase))
            {
                var best = group.OrderBy(a => Array.IndexOf(rids, a.Runtime)).First();
                runtime[group.Key] = new RuntimeFile(Full(best.Path), group.Key);
            }

            generators.AddRange(PackageGenerators(packageDir, languageFolder));
        }

        return new RestoredPackages(compile, [.. runtime.Values], generators, [], warnings);
    }

    private static string? FindPackageDirectory(LockFile lockFile, LockFileTargetLibrary library, IReadOnlyList<string> folders)
    {
        var path = lockFile.GetLibrary(library.Name, library.Version)?.Path
                   ?? Path.Combine(library.Name!.ToLowerInvariant(), library.Version!.ToNormalizedString().ToLowerInvariant());
        return folders.Select(f => Path.Combine(f, path)).FirstOrDefault(Directory.Exists);
    }

    // "_._" marks "nothing for this framework" in a lock file.
    private static bool IsRealAsset(LockFileItem item) => !item.Path.EndsWith("_._", StringComparison.Ordinal);

    // analyzers/dotnet/<lang>/ (and analyzers/dotnet/roslynX.Y/<lang>/ — the highest one this Roslyn can
    // load), like the SDK's ResolvePackageAssets for analyzers. Only source generators are used.
    private static IEnumerable<string> PackageGenerators(string packageDir, string languageFolder)
    {
        var dotnet = Path.Combine(packageDir, "analyzers", "dotnet");
        if (!Directory.Exists(dotnet)) return [];

        var roslyn = typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version ?? new Version(99, 0);
        var versioned = Directory.GetDirectories(dotnet, "roslyn*")
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d)["roslyn".Length..], out var v) ? v : null))
            .Where(p => p.Version is { } v && v <= roslyn)
            .OrderByDescending(p => p.Version)
            .Select(p => Path.Combine(p.Dir, languageFolder))
            .FirstOrDefault(Directory.Exists);

        var dir = versioned ?? Path.Combine(dotnet, languageFolder);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dll").Where(TargetingPack.IsManagedAssembly) : [];
    }

    // The current platform's RID and its portable fallbacks, most specific first (win-x64 -> win).
    // The runtime's own RID first, then the portable one packages ship assets for: a .NET built by a
    // Linux distribution reports its own RID (ubuntu.24.04-x64), which no package has a folder for,
    // so SQLite's runtimes/linux-x64/native/libe_sqlite3.so would otherwise never be placed.
    internal static string[] RuntimeIdentifierFallbacks() =>
        RuntimeIdentifierFallbacks(RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture);

    internal static string[] RuntimeIdentifierFallbacks(string rid, Architecture architecture)
    {
        var arch = architecture.ToString().ToLowerInvariant();
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : rid.Contains("musl", StringComparison.Ordinal) || rid.StartsWith("alpine", StringComparison.Ordinal) ? "linux-musl"
            : "linux";
        string[] all = OperatingSystem.IsWindows()
            ? [rid, $"{os}-{arch}", rid.Split('-')[0], os, "any"]
            : [rid, $"{os}-{arch}", rid.Split('-')[0], os, os == "linux-musl" ? "linux" : "unix", "unix", "any"];
        return [.. all.Distinct(StringComparer.Ordinal)];
    }

    private static ResultItem Message(string code, string text) => new(code, "", new LineSymbol(0, 0), text);

    private static async Task<Stream> CopyToMemoryAsync(IPackageDownloader downloader, CancellationToken ct)
    {
        var memory = new MemoryStream();
        var temp = Path.Combine(Path.GetTempPath(), "runpad-pack-" + Guid.NewGuid() + ".nupkg");
        try
        {
            if (!await downloader.CopyNupkgFileToAsync(temp, ct))
                throw new IOException("The package couldn't be downloaded.");
            await using (var file = File.OpenRead(temp))
                await file.CopyToAsync(memory, ct);
        }
        finally
        {
            File.Delete(temp);
        }
        memory.Position = 0;
        return memory;
    }

    // Restore errors/warnings (NU1101 package not found, NU1603, ...) as compiler-style items.
    private sealed class CollectingLogger : LoggerBase
    {
        public List<ResultItem> Errors { get; } = [];
        public List<ResultItem> Warnings { get; } = [];

        public override void Log(ILogMessage message)
        {
            var code = message.Code == NuGetLogCode.Undefined ? "" : message.Code.ToString();
            lock (this)
            {
                if (message.Level == LogLevel.Error)
                    Errors.Add(Message(code, message.Message));
                else if (message.Level == LogLevel.Warning)
                    Warnings.Add(Message(code, message.Message));
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
