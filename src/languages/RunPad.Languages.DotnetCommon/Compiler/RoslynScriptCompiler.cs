using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace RunPad.Languages.DotnetCommon.Compiler;

/// <summary>The target a script compiles for: its framework and the matching targeting pack.</summary>
public sealed record ScriptTarget(string TargetFramework, int Major, int Minor, TargetingPack Pack);

// Compiles a script in-process with Roslyn — RunPad no longer uses MSBuild at all (docs/handoffs/
// roslyn-script-compiler.md). It reproduces what `dotnet build` of a throwaway project gave, so running,
// debugging (netcoredbg), the IL tab and the decompiler see the same OutputDirectory layout:
//   OutputDirectory/RunPadQuery.<ext>               the compiled script (breakpoints map to it)
//   OutputDirectory/bin/RunPadQuery.dll + .pdb      portable PDB, sources embedded (EmbedAllSources)
//   OutputDirectory/bin/RunPadQuery.runtimeconfig.json
//   OutputDirectory/bin/...                         RunPad.Runtime, referenced DLLs (+ same-folder
//                                                   dependencies), NuGet packages' runtime assets
// NuGet packages are restored in-process (NuGetRestorer). Project references are not supported.
// Language specifics (parse/compile options, generated source) live in the subclasses.
//
// Known difference from `dotnet build`: the SDK's code-analysis (CA*) and NETSDK warnings aren't
// produced, and packages' MSBuild .props/.targets don't run. Compiler diagnostics are the same.
public abstract class RoslynScriptCompiler : IDotnetProcessCompiler
{
    // Bumped whenever the output for the same inputs changes, so the hot path never serves a binary an
    // older RunPad produced.
    private const string CompilerRevision = "2";
    private const string BinDir = "bin";

    // csc/vbc run by the SDK with /preferreduilang:en-US: messages are English whatever the OS language.
    protected static readonly CultureInfo MessageCulture = CultureInfo.GetCultureInfo("en-US");

    private static readonly ConcurrentDictionary<(string Path, DateTime Stamp), MetadataReference> References = new();
    private static readonly ConcurrentDictionary<(string Path, DateTime Stamp), ImmutableArray<ISourceGenerator>> Generators = new();
    private static readonly ConcurrentDictionary<(string Path, DateTime Stamp), Version?> AssemblyVersions = new();

    private readonly ILogger _logger;
    private readonly string _outputDir;
    private readonly string _objDir;
    private readonly string _sourcePath;
    private readonly string _binDir;
    private readonly string _dllPath;
    private readonly string _pdbPath;

    protected CompiledDotnetLanguageHostOptions Options { get; }

    protected RoslynScriptCompiler(CompiledDotnetLanguageHostOptions options, ILogger logger)
    {
        Options = options;
        _logger = logger;
        _outputDir = options.OutputDirectory;
        _objDir = Path.Combine(_outputDir, "obj");
        _sourcePath = Path.Combine(_outputDir, options.OutputName + SourceFileExtension);
        _binDir = Path.Combine(_outputDir, BinDir);
        _dllPath = Path.Combine(_binDir, options.OutputName + ".dll");
        _pdbPath = Path.Combine(_binDir, options.OutputName + ".pdb");
    }

    // ── Language specifics ───────────────────────────────────────────────────

    protected abstract string SourceFileExtension { get; }

    /// <summary>"cs" or "vb": the analyzers/dotnet/&lt;folder&gt; generators come from.</summary>
    protected abstract string LanguageFolder { get; }

    protected abstract string FormatImport(string ns);

    protected abstract SyntaxTree Parse(SourceText text, string path, ScriptTarget target, CancellationToken ct);

    /// <summary>What the SDK generates into obj/ (implicit usings, TargetFrameworkAttribute).</summary>
    protected abstract string GeneratedSource(ScriptTarget target);

    protected abstract Compilation CreateCompilation(
        string assemblyName, IReadOnlyList<SyntaxTree> trees, IEnumerable<MetadataReference> references, ScriptTarget target);

    protected abstract GeneratorDriver CreateGeneratorDriver(ImmutableArray<ISourceGenerator> generators, ParseOptions parseOptions);

    // ── Pipeline ─────────────────────────────────────────────────────────────

    // CPU-bound and blocking IO (parse, bind, emit, restore): off the caller's (often the UI) thread.
    public Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx) =>
        Task.Run(() => CompileAsync(code, ctx), ctx);

    public void Dispose()
    {
    }

    private async Task<ProcessCompilerResult> CompileAsync(string code, CancellationToken ct)
    {
        if (Options.ProjectReferences.Count > 0)
            return Failed(Message("",
                "Project references are no longer supported: " + string.Join(", ", Options.ProjectReferences.Select(Path.GetFileName)) +
                ". Reference the project's built .dll instead."));

        // Front-matter namespaces become import lines at the top of the compiled file, exactly as the old
        // MSBuild path did — the hosts' CalcKindOffset counts them for breakpoint lines.
        if (Options.Usings.Count > 0)
            code = string.Join("\n", Options.Usings.Select(FormatImport)) + "\n" + code;

        var tfm = Options.Platform.TargetFrameworkMoniker;
        if (!TargetFramework.TryParse(tfm, out var major, out var minor))
            return Failed(Message("", $"'{tfm}' is not a supported target framework."));

        var settings = NuGetRestorer.LoadSettings(Options.NuGetConfigFile, _outputDir);
        var globalPackages = SettingsUtility.GetGlobalPackagesFolder(settings);
        var runtimePath = Options.ExecutionOptions.Runtime.RuntimePath;
        var pack = TargetingPack.Find(tfm, runtimePath, globalPackages);
        if (pack is null && await NuGetRestorer.DownloadTargetingPackAsync(tfm, Options.NuGetConfigFile, _objDir, ct) is not null)
            pack = TargetingPack.Find(tfm, runtimePath, globalPackages);
        if (pack is null)
            return Failed(Message("", $"The {TargetingPack.PackageId} targeting pack for {tfm} isn't installed and couldn't be downloaded."));
        var target = new ScriptTarget(tfm, major, minor, pack);

        var packages = await NuGetRestorer.RestoreAsync(Options.Packages, tfm, LanguageFolder, _objDir, Options.NuGetConfigFile, ct);
        if (packages.Errors.Count > 0)
            return new ProcessCompilerResult(_dllPath, false, packages.Errors, packages.Warnings);

        Directory.CreateDirectory(_binDir);
        var userReferences = ResolveUserReferences();
        var (frameworkReferences, packageReferences) = ResolveConflicts(pack.ReferenceAssemblies.ToList(), packages.CompileReferences);
        var runtimeFiles = packages.RuntimeFiles.Where(f => !IsProvidedByFramework(f, pack)).ToList();
        var generatorPaths = pack.GeneratorAssemblies(LanguageFolder).Concat(packages.GeneratorAssemblies).ToList();

        var key = Fingerprint(code, pack, userReferences, packageReferences, runtimeFiles, generatorPaths);
        var keyPath = Path.Combine(_objDir, Options.OutputName + ".roslyn.key");
        if (IsUpToDate(keyPath, key))
            return new ProcessCompilerResult(_dllPath, true, [], packages.Warnings);

        // The script file itself — what breakpoints and the debugger's source view point at.
        if (!File.Exists(_sourcePath) || await File.ReadAllTextAsync(_sourcePath, ct) != code)
            await File.WriteAllTextAsync(_sourcePath, code, Encoding.UTF8, ct);

        var scriptText = SourceText.From(code, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        var scriptTree = Parse(scriptText, _sourcePath, target, ct);
        var generatedPath = Path.Combine(_objDir, Options.OutputName + ".Generated" + SourceFileExtension);
        var generatedTree = Parse(SourceText.From(GeneratedSource(target), Encoding.UTF8, SourceHashAlgorithm.Sha256),
            generatedPath, target, ct);

        var references = frameworkReferences.Concat([Options.RuntimeLibPath]).Concat(userReferences).Concat(packageReferences)
            .Select(GetReference);
        var compilation = CreateCompilation(Options.OutputName, [scriptTree, generatedTree], references, target);

        var diagnostics = new List<RoslynDiagnostic>();
        var generators = generatorPaths.SelectMany(p => GetGenerators(p)).ToImmutableArray();
        if (!generators.IsEmpty)
        {
            CreateGeneratorDriver(generators, scriptTree.Options)
                .RunGeneratorsAndUpdateCompilation(compilation, out compilation, out var generatorDiagnostics, ct);
            diagnostics.AddRange(generatorDiagnostics);
        }

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = compilation.Emit(
            peStream, pdbStream,
            embeddedTexts: EmbeddedTexts(compilation),
            // pdbFilePath goes into the PE's CodeView entry: without it netcoredbg reports
            // "Symbols not found" (docs/archive/ANALYSIS-2026-07.md, feature #16 of the old fast path).
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: _pdbPath),
            cancellationToken: ct);
        diagnostics.AddRange(emit.Diagnostics);

        var errors = ToResultItems(diagnostics, RoslynSeverity.Error);
        var warnings = packages.Warnings.Concat(ToResultItems(diagnostics, RoslynSeverity.Warning)).ToList();
        if (!emit.Success)
        {
            // Like a failed `dotnet build`: no stale binary from an earlier run is left to execute.
            TryDelete(_dllPath);
            TryDelete(keyPath);
            return new ProcessCompilerResult(_dllPath, false, errors, warnings);
        }

        try
        {
            await File.WriteAllBytesAsync(_dllPath, peStream.ToArray(), ct);
            await File.WriteAllBytesAsync(_pdbPath, pdbStream.ToArray(), ct);
            CopyWithRelatedFiles(Options.RuntimeLibPath);
            foreach (var reference in userReferences)
                CopyWithRelatedFiles(reference);
            foreach (var file in runtimeFiles)
                CopyIfChanged(file.SourcePath, Path.Combine(_binDir, file.RelativePath));
            await File.WriteAllTextAsync(Path.Combine(_binDir, Options.OutputName + ".runtimeconfig.json"),
                RuntimeConfig(tfm, Options.Platform.RuntimeConfigVersion), ct);
            // Left by a build of an older RunPad (MSBuild); it would name assemblies this one doesn't ship.
            TryDelete(Path.Combine(_binDir, Options.OutputName + ".deps.json"));
            Directory.CreateDirectory(_objDir);
            await File.WriteAllTextAsync(keyPath, key + "|" + File.GetLastWriteTimeUtc(_dllPath).Ticks, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Writing the compiled script failed");
            TryDelete(keyPath);
            return new ProcessCompilerResult(_dllPath, false,
                [.. errors, Message("", $"Couldn't write the compiled script: {ex.Message}")], warnings);
        }

        return new ProcessCompilerResult(_dllPath, true, errors, warnings);
    }

    private ProcessCompilerResult Failed(ResultItem error) => new(_dllPath, false, [error], []);

    protected static ResultItem Message(string code, string text) => new(code, "", new LineSymbol(0, 0), text);

    // ── References ───────────────────────────────────────────────────────────

    // Front-matter .dll references plus the dependencies `dotnet build` would find next to them
    // (ResolveAssemblyReference's FindDependencies): same folder, recursively.
    private List<string> ResolveUserReferences()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(Options.AssemblyReferences.Where(File.Exists).Select(Path.GetFullPath));
        while (pending.Count > 0)
        {
            var path = pending.Dequeue();
            if (!seen.Add(Path.GetFileName(path))) continue;
            result.Add(path);
            foreach (var dependency in SameFolderDependencies(path))
                pending.Enqueue(dependency);
        }
        return result;
    }

    private static IEnumerable<string> SameFolderDependencies(string assemblyPath)
    {
        var directory = Path.GetDirectoryName(assemblyPath)!;
        List<string> names;
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) yield break;
            var reader = pe.GetMetadataReader();
            names = [.. reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))];
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var name in names)
        {
            var candidate = Path.Combine(directory, name + ".dll");
            if (File.Exists(candidate) && TargetingPack.IsManagedAssembly(candidate))
                yield return candidate;
        }
    }

    // A package can ship an assembly the framework already has (System.Text.Json, ...): like the SDK's
    // conflict resolution, the higher assembly version wins and the other one isn't referenced.
    private static (List<string> Framework, List<string> Packages) ResolveConflicts(
        List<string> framework, IReadOnlyList<string> packages)
    {
        var frameworkByName = framework.ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        var keptPackages = new List<string>();
        foreach (var package in packages)
        {
            if (frameworkByName.TryGetValue(Path.GetFileName(package), out var frameworkPath))
            {
                if (VersionOf(package) is { } packageVersion && VersionOf(frameworkPath) is { } frameworkVersion
                    && packageVersion <= frameworkVersion)
                    continue;
                framework.Remove(frameworkPath);
            }
            keptPackages.Add(package);
        }
        return (framework, keptPackages);
    }

    // The framework already ships this assembly at the same or a higher version: the app-local copy
    // would be ignored at run time anyway, so (like the SDK) it isn't copied.
    private static bool IsProvidedByFramework(RuntimeFile file, TargetingPack pack)
    {
        if (!file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Contains(Path.DirectorySeparatorChar))
            return false;
        var frameworkPath = Path.Combine(pack.ReferenceDirectory, file.RelativePath);
        return File.Exists(frameworkPath)
               && VersionOf(file.SourcePath) is { } packageVersion && VersionOf(frameworkPath) is { } frameworkVersion
               && packageVersion <= frameworkVersion;
    }

    private static Version? VersionOf(string path) =>
        AssemblyVersions.GetOrAdd((path, File.GetLastWriteTimeUtc(path)), key =>
        {
            try
            {
                return AssemblyName.GetAssemblyName(key.Path).Version;
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or FileLoadException)
            {
                return null;
            }
        });

    private static MetadataReference GetReference(string path) =>
        References.GetOrAdd((path, File.GetLastWriteTimeUtc(path)), key => MetadataReference.CreateFromFile(key.Path));

    private ImmutableArray<ISourceGenerator> GetGenerators(string path) =>
        Generators.GetOrAdd((path, File.GetLastWriteTimeUtc(path)), key =>
        {
            try
            {
                return new AnalyzerFileReference(key.Path, GeneratorAssemblyLoader.Instance).GetGenerators(LanguageName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't load source generators from {Path}", key.Path);
                return [];
            }
        });

    private string LanguageName => LanguageFolder == "vb" ? LanguageNames.VisualBasic : LanguageNames.CSharp;

    // ── Incremental build ────────────────────────────────────────────────────

    private string Fingerprint(string code, TargetingPack pack, IEnumerable<string> userReferences,
        IEnumerable<string> packageReferences, IEnumerable<RuntimeFile> runtimeFiles, IEnumerable<string> generators)
    {
        var builder = new StringBuilder()
            .Append(CompilerRevision).Append('\n')
            .Append(Options.Platform.TargetFrameworkMoniker).Append('\n')
            .Append(pack.ReferenceDirectory).Append('\n');
        foreach (var path in userReferences.Concat(packageReferences).Concat(generators).Prepend(Options.RuntimeLibPath))
            builder.Append(path).Append('|').Append(File.GetLastWriteTimeUtc(path).Ticks).Append('\n');
        foreach (var file in runtimeFiles)
            builder.Append(file.SourcePath).Append("->").Append(file.RelativePath).Append('\n');
        builder.Append(code);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    // Same inputs, and the binary is still the one this compiler wrote.
    private bool IsUpToDate(string keyPath, string key)
    {
        try
        {
            if (!File.Exists(keyPath) || !File.Exists(_dllPath) || !File.Exists(_pdbPath)) return false;
            var stored = File.ReadAllText(keyPath).Split('|');
            return stored.Length == 2 && stored[0] == key
                   && stored[1] == File.GetLastWriteTimeUtc(_dllPath).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ── Output ───────────────────────────────────────────────────────────────

    // The SDK's DEBUG/TRACE and target-framework symbols (NET, NET8_0, NET8_0_OR_GREATER, ...).
    protected static IEnumerable<string> FrameworkSymbols(int major, int minor)
    {
        yield return "NET";
        yield return "NETCOREAPP";
        yield return $"NET{major}_{minor}";
        for (var m = 5; m <= major; m++)
            yield return $"NET{m}_0_OR_GREATER";
        foreach (var legacy in new[] { "1_0", "1_1", "2_0", "2_1", "2_2", "3_0", "3_1" })
            yield return $"NETCOREAPP{legacy}_OR_GREATER";
    }

    private static string RuntimeConfig(string tfm, string frameworkVersion) => $$"""
        {
          "runtimeOptions": {
            "tfm": "{{tfm}}",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "{{frameworkVersion}}"
            }
          }
        }
        """;

    // <EmbedAllSources>: every tree with a real encoding (the script, the generated file, generator
    // output), so the debugger can show any of them.
    private static IEnumerable<EmbeddedText> EmbeddedTexts(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var text = tree.GetText();
            if (text.Encoding is not null && !string.IsNullOrEmpty(tree.FilePath))
                yield return EmbeddedText.FromSource(tree.FilePath, text);
        }
    }

    // ResultItem shape of the old MSBuild output parser: file relative to OutputDirectory (with its
    // leading separator), 1-based line/column, 0/0 when there's no source position.
    private List<ResultItem> ToResultItems(IEnumerable<RoslynDiagnostic> diagnostics, RoslynSeverity severity)
    {
        var items = new List<ResultItem>();
        var seen = new HashSet<(string, string, uint, uint, string)>();
        foreach (var d in diagnostics.Where(d => d.Severity == severity && !d.IsSuppressed))
        {
            var message = d.GetMessage(MessageCulture);
            ResultItem item;
            if (d.Location.IsInSource)
            {
                var span = d.Location.GetMappedLineSpan();
                item = new ResultItem(d.Id, span.Path.Replace(_outputDir, ""),
                    new LineSymbol((uint)span.StartLinePosition.Line + 1, (uint)span.StartLinePosition.Character + 1), message);
            }
            else
                item = Message(d.Id, message);
            if (seen.Add((item.Code, item.File, item.LineSymbol.Line, item.LineSymbol.Symbol, item.Message)))
                items.Add(item);
        }
        return items;
    }

    private void CopyWithRelatedFiles(string path)
    {
        var target = Path.Combine(_binDir, Path.GetFileName(path));
        CopyIfChanged(path, target);
        // Like `dotnet build`'s related files: the debugger finds a reference's symbols, the editor its docs.
        foreach (var related in new[] { ".pdb", ".xml" })
        {
            var source = Path.ChangeExtension(path, related);
            if (File.Exists(source))
                CopyIfChanged(source, Path.ChangeExtension(target, related));
        }
    }

    private static void CopyIfChanged(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        if (targetInfo.Exists && targetInfo.Length == sourceInfo.Length && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        File.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover only costs a rebuild later.
        }
    }
}
