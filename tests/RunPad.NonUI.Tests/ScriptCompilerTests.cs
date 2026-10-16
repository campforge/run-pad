using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts.Models;
using RunPad.Debug;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.Compiler;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Compiler;
using RunPad.Languages.VB;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

// Scripts compile in-process with Roslyn (CSharpScriptCompiler,
// VbScriptCompiler); RunPad no longer uses MSBuild. Real compilations and real script processes. The
// expectations reproduce what the old `dotnet build` path gave (several of these were MSBuild-era
// regression tests, ported here).
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
public sealed class ScriptCompilerTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-script-compiler").FullName;
    private int _dirs;

    // Bounded retry for a briefly locked bin/RunPad.Runtime.dll on Windows — see TempDirectoryCleanup.
    public ValueTask DisposeAsync() => TempDirectoryCleanup.DeleteAsync(_root);

    private static DotnetSdk LatestSdk => new DotnetSdkProvider().Get().First();

    private CompiledDotnetLanguageHostOptions Options(
        DotnetSdk? sdk = null, RpadKind kind = RpadKind.Statements, IReadOnlyList<string>? usings = null,
        IReadOnlyList<string>? assemblyReferences = null, IReadOnlyList<string>? projectReferences = null,
        string? runtimeLib = null, string? outputDirectory = null)
    {
        var dir = outputDirectory ?? Directory.CreateDirectory(Path.Combine(_root, $"out{++_dirs}")).FullName;
        return new CompiledDotnetLanguageHostOptions
        {
            Platform = sdk ?? LatestSdk,
            ExecutionOptions = new ExecutionOptions
            {
                WorkingDirectory = dir,
                Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                Debugger = new DebuggerOptions { DebuggerPath = "" },
            },
            OutputDirectory = dir,
            RuntimeLibPath = runtimeLib ?? new CSharpReferenceService().GetExtensionReference(),
            Kind = kind,
            Usings = usings ?? [],
            AssemblyReferences = assemblyReferences ?? [],
            ProjectReferences = projectReferences ?? [],
        };
    }

    private static RoslynScriptCompiler CSharp(CompiledDotnetLanguageHostOptions options) =>
        new CSharpScriptCompiler(options, NullLogger.Instance);

    private static RoslynScriptCompiler Vb(CompiledDotnetLanguageHostOptions options) =>
        new VbScriptCompiler(options, NullLogger.Instance);

    private static async Task<List<string>> RunAsync(CompiledDotnetLanguageHostOptions options, string code, bool vb = false)
    {
        await using CompiledDotnetLanguageHostBase host = vb
            ? new VbLanguageHost(options, NullLoggerFactory.Instance)
            : new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        var output = new List<string>();
        await host.RunAsync(code, (data, kind) => { lock (output) output.Add($"{kind}: {data}"); },
            TestContext.Current.CancellationToken);
        return output;
    }

    private static string Errors(ProcessCompilerResult result) => string.Join("\n", result.Errors.Select(e => $"{e.Code} {e.Message}"));

    // Compiler errors stay English on a Russian OS, for both languages, like the SDK with
    // /preferreduilang:en-US. The collection runs alone, so changing the process cultures is safe.
    [Theory]
    [InlineData(false, "CS0103", "The name 'missing' does not exist in the current context")]
    [InlineData(true, "BC30451", "'missing' is not declared.")]
    public async Task CompileErrors_Should_BeInEnglish_UnderARussianUiCulture(bool vb, string code, string message)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var (defaultCulture, defaultUiCulture) = (CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        var russian = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = russian;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = russian;
            using var compiler = vb ? Vb(Options()) : CSharp(Options());

            var result = await compiler.Compilation(
                vb ? "Module Program\nSub Main()\nConsole.WriteLine(missing)\nEnd Sub\nEnd Module\n" : "Console.WriteLine(missing);\n",
                TestContext.Current.CancellationToken);

            var error = Assert.Single(result.Errors);
            Assert.Equal(code, error.Code);
            Assert.Contains(message, error.Message, StringComparison.Ordinal);
            Assert.Equal("ru-RU", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = defaultUiCulture;
        }
    }

    // Document paths recorded in the PDB, and whether each one's source is embedded.
    private static List<(string Name, bool Embedded)> PdbDocuments(string pdbPath)
    {
        var embeddedSource = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
        using var pdb = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath));
        var reader = pdb.GetMetadataReader();
        return [.. reader.Documents.Select(h => (
            reader.GetString(reader.GetDocument(h).Name),
            reader.GetCustomDebugInformation(h).Any(c => reader.GetGuid(reader.GetCustomDebugInformation(c).Kind) == embeddedSource)))];
    }

    // An OutputDirectory reached through a directory symlink, like macOS's /var → /private/var: the
    // alias and real spellings of the same directory. Unix only (links stay unresolved on Windows).
    private (string Alias, string Real) SymlinkedOutputDirectory(string name)
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, name + "-real", "out")).FullName;
        var link = Path.Combine(_root, name + "-alias");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(real)!);
        return (Path.Combine(link, "out"), real);
    }

    // Copies the files with their timestamps, so a cache key stamped with the DLL's still matches.
    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
        }
    }

    // ── Output layout ────────────────────────────────────────────────────────

    [Fact]
    public async Task Compilation_Should_ProduceTheScriptLayout_WithADebuggablePdb()
    {
        var options = Options();
        const string code = "var x = 41;\nx++;\nConsole.WriteLine(x);\n";
        using var compiler = CSharp(options);

        var result = await compiler.Compilation(code, TestContext.Current.CancellationToken);

        Assert.True(result.Success, Errors(result));
        var dir = options.OutputDirectory;
        var sourcePath = Path.Combine(dir, "RunPadQuery.cs");
        var pdbPath = Path.Combine(dir, "bin", "RunPadQuery.pdb");
        Assert.Equal(Path.Combine(dir, "bin", "RunPadQuery.dll"), result.OutputBinaryFile);
        Assert.Equal(code, File.ReadAllText(sourcePath));
        Assert.True(File.Exists(Path.Combine(dir, "bin", "RunPad.Runtime.dll")));
        Assert.Contains($"\"tfm\": \"{options.Platform.TargetFrameworkMoniker}\"",
            File.ReadAllText(Path.Combine(dir, "bin", "RunPadQuery.runtimeconfig.json")));

        // netcoredbg pairs the DLL with its PDB through the CodeView entry, and maps breakpoints set on
        // OutputDirectory/RunPadQuery.cs through the PDB's document table; the script is embedded.
        using (var pe = new PEReader(File.OpenRead(result.OutputBinaryFile)))
        {
            var codeView = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
            Assert.Equal(pdbPath, pe.ReadCodeViewDebugDirectoryData(codeView).Path);
        }
        // The document is the canonical spelling DapSession sends breakpoints for (on macOS the temp
        // directory /var/... becomes /private/var/...; unchanged on Windows).
        Assert.Contains((DebugSourcePath.Canonicalize(sourcePath), true), PdbDocuments(pdbPath));
    }

    // The PDB recorded OutputDirectory as given while
    // DapSession canonicalizes the breakpoint source, so on macOS (/var → /private/var) netcoredbg left
    // every breakpoint pending. A directory symlink reproduces that on any Unix CI without netcoredbg.
    [Theory]
    [InlineData(false, "var x = 41;\nx++;\nConsole.WriteLine(x);\n")]
    [InlineData(true, "Dim x = 41\nx += 1\nConsole.WriteLine(x)\n")]
    public async Task Pdb_Should_RecordTheCanonicalScriptPath_WhenOutputDirectoryIsASymlink(bool vb, string code)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var (alias, real) = SymlinkedOutputDirectory(vb ? "vb" : "cs");
        var options = Options(outputDirectory: alias);
        using var compiler = vb ? Vb(options) : CSharp(options);

        var result = await compiler.Compilation(vb ? $"Module Program\nSub Main()\n{code}End Sub\nEnd Module\n" : code,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, Errors(result));
        var script = "RunPadQuery" + (vb ? ".vb" : ".cs");
        var breakpointSource = DebugSourcePath.Canonicalize(Path.Combine(alias, script));
        Assert.Equal(DebugSourcePath.Canonicalize(Path.Combine(real, script)), breakpointSource);
        var documents = PdbDocuments(Path.Combine(alias, "bin", "RunPadQuery.pdb"));
        Assert.Contains((breakpointSource, true), documents);
        Assert.All(documents, d => Assert.DoesNotContain(alias, d.Name));
    }

    // Diagnostics still come back relative to OutputDirectory although the compiler sees the canonical path.
    [Fact]
    public async Task Diagnostics_Should_StayRelative_WhenOutputDirectoryIsASymlink()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        var (alias, _) = SymlinkedOutputDirectory("diag");
        using var compiler = CSharp(Options(outputDirectory: alias));

        var result = await compiler.Compilation("Console.WriteLine(y);\n", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(Path.DirectorySeparatorChar + "RunPadQuery.cs", Assert.Single(result.Errors).File);
    }

    // The canonical document path is part of the cache key: otherwise up-to-date outputs whose PDB
    // records another real directory (an alias retargeted, here to a copy of the outputs) would be reused.
    [Fact]
    public async Task CachedBinary_Should_BeRebuilt_WhenTheCanonicalScriptPathChanges()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Paths are left unchanged on Windows.");
        const string code = "Console.WriteLine(1);";
        var (alias, real) = SymlinkedOutputDirectory("cache");
        var options = Options(outputDirectory: alias);
        using (var compiler = CSharp(options))
            Assert.True((await compiler.Compilation(code, TestContext.Current.CancellationToken)).Success);

        var moved = Path.Combine(_root, "cache-moved", "out");
        CopyDirectory(real, moved);
        var link = Path.GetDirectoryName(alias)!;
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(moved)!);
        var pdbPath = Path.Combine(alias, "bin", "RunPadQuery.pdb");
        Assert.Contains((DebugSourcePath.Canonicalize(Path.Combine(real, "RunPadQuery.cs")), true), PdbDocuments(pdbPath));

        using (var compiler = CSharp(options))
            Assert.True((await compiler.Compilation(code, TestContext.Current.CancellationToken)).Success);

        var documents = PdbDocuments(pdbPath);
        Assert.Contains((DebugSourcePath.Canonicalize(Path.Combine(moved, "RunPadQuery.cs")), true), documents);
        Assert.DoesNotContain(documents, d => d.Name.StartsWith(DebugSourcePath.Canonicalize(real) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    // The shape the old MSBuild output parser produced: file relative to OutputDirectory, 1-based
    // line/column; front-matter usings move the script down one line each; English like csc's
    // /preferreduilang:en-US, whatever the OS language.
    [Fact]
    public async Task Diagnostics_Should_KeepTheMsBuildShape_AndBeEnglish()
    {
        var culture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            using var compiler = CSharp(Options(usings: ["System.Text"]));
            var result = await compiler.Compilation("var x = 1;\nConsole.WriteLine(y);\n", TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            var file = Path.DirectorySeparatorChar + "RunPadQuery.cs";
            Assert.Equal(new ResultItem("CS0103", file, new LineSymbol(3, 19), "The name 'y' does not exist in the current context"),
                Assert.Single(result.Errors));
            Assert.Contains(result.Warnings, w => w is { Code: "CS0219", LineSymbol: { Line: 2, Symbol: 5 } });
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
        }
    }

    // A script with no entry point: CS5001 has no source position (line/column 0), and is reported once.
    [Fact]
    public async Task ErrorsWithoutASourcePosition_Should_StillBeReported_Once()
    {
        using var compiler = CSharp(Options());

        var result = await compiler.Compilation("namespace X { class Y { } }", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Equal("CS5001", error.Code);
        Assert.Equal(new LineSymbol(0, 0), error.LineSymbol);
    }

    // `dotnet build` passes /warnaserror+:SYSLIB0011: BinaryFormatter is an error, not a warning.
    [Fact]
    public async Task BinaryFormatter_Should_BeAnError_LikeTheSdk()
    {
        using var compiler = CSharp(Options());

        var result = await compiler.Compilation(
            "var f = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == "SYSLIB0011");
    }

    // ── Stale and up-to-date binaries ────────────────────────────────────────

    [Theory]
    [InlineData(false, "Console.WriteLine(1);", "this is not C#")]
    [InlineData(true, "Module Program\nSub Main()\nConsole.WriteLine(1)\nEnd Sub\nEnd Module\n", "this is not valid VB {{{")]
    public async Task FailedCompilation_Should_LeaveNoStaleBinaryToRun(bool vb, string good, string broken)
    {
        var options = Options();
        using var compiler = vb ? Vb(options) : CSharp(options);
        var first = await compiler.Compilation(good, TestContext.Current.CancellationToken);
        Assert.True(first.Success, Errors(first));

        var second = await compiler.Compilation(broken, TestContext.Current.CancellationToken);
        var third = await compiler.Compilation(broken, TestContext.Current.CancellationToken);

        Assert.False(second.Success);
        Assert.False(third.Success);
        Assert.False(File.Exists(second.OutputBinaryFile));
    }

    [Fact]
    public async Task UnchangedScript_Should_NotBeRecompiled_AndAChange_Should()
    {
        using var compiler = CSharp(Options());
        var first = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);
        var stamp = File.GetLastWriteTimeUtc(first.OutputBinaryFile);

        await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(first.OutputBinaryFile));

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await compiler.Compilation("Console.WriteLine(2);", TestContext.Current.CancellationToken);
        Assert.NotEqual(stamp, File.GetLastWriteTimeUtc(first.OutputBinaryFile));
    }

    // Updating RunPad rebuilds RunPad.Runtime.dll in place (same path): the script must be rebuilt
    // against it rather than keep running against the old copy in bin/.
    [Fact]
    public async Task ARebuiltRuntimeLibrary_Should_ForceARebuild_EvenIfTheScriptDidntChange()
    {
        var libDir = Directory.CreateDirectory(Path.Combine(_root, "runtime-lib")).FullName;
        var runtimeLib = Path.Combine(libDir, "RunPad.Runtime.dll");
        File.Copy(new CSharpReferenceService().GetExtensionReference(), runtimeLib);
        using var compiler = CSharp(Options(runtimeLib: runtimeLib));
        var first = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);
        var stamp = File.GetLastWriteTimeUtc(first.OutputBinaryFile);

        File.SetLastWriteTimeUtc(runtimeLib, DateTime.UtcNow.AddMinutes(5));
        var second = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);

        Assert.True(second.Success, Errors(second));
        Assert.NotEqual(stamp, File.GetLastWriteTimeUtc(second.OutputBinaryFile));
    }

    // ── Language defaults ────────────────────────────────────────────────────

    // LINQPad Runtime API compatibility (2026-07-28): DumpContainer/ICustomMemberProvider by short name.
    [Fact]
    public async Task CSharp_Should_SeeRunPadRuntimeTypes_WithoutAUsing()
    {
        using var compiler = CSharp(Options());

        var result = await compiler.Compilation("""
            var dc = new DumpContainer("hello");
            dc.Dump();
            Console.WriteLine(dc.Content);
            class Provider : ICustomMemberProvider
            {
                public IEnumerable<string> GetNames() => new[] { "A" };
                public IEnumerable<Type> GetTypes() => new[] { typeof(int) };
                public IEnumerable<object?> GetValues() => new object?[] { 1 };
            }
            """, TestContext.Current.CancellationToken);

        Assert.True(result.Success, Errors(result));
    }

    // vbc settings of the SDK: RunPad.Runtime + SDK default imports, Option Strict Custom (implicit
    // narrowing allowed), Option Infer, root namespace = assembly name, CONFIG/_MyType defines.
    [Fact]
    public async Task Vb_Should_UseTheSdkDefaults()
    {
        var output = await RunAsync(Options(kind: RpadKind.Program), """
            Module M
                Sub Main()
                    Dim dc = New DumpContainer("hello")
                    Dim narrowed As Integer = 3.7
                    Dim inferred = New List(Of Integer) From {1, 2, 3}
                    Console.WriteLine(GetType(M).FullName & " " & narrowed & " " & inferred.Sum() & " " & dc.Content.ToString())
            #If CONFIG = "Debug" AndAlso _MyType = "Empty" AndAlso NET8_0_OR_GREATER Then
                    Console.WriteLine("defines ok")
            #End If
                End Sub
            End Module
            """, vb: true);

        Assert.Contains("StandardOutput: RunPadQuery.M 4 6 hello", output);
        Assert.Contains("StandardOutput: defines ok", output);
    }

    // ── Target framework, generators, references ─────────────────────────────

    public static TheoryData<string> InstalledTargetFrameworks()
    {
        var data = new TheoryData<string>();
        foreach (var tfm in new DotnetSdkProvider().Get().Select(s => s.TargetFrameworkMoniker).Distinct())
            data.Add(tfm);
        return data;
    }

    // Compiled against the TARGET's reference assemblies and run on the target runtime — not on the
    // .NET RunPad itself runs on.
    [Theory]
    [MemberData(nameof(InstalledTargetFrameworks))]
    public async Task Script_Should_CompileAgainstAndRunOnItsTargetFramework(string tfm)
    {
        var sdk = new DotnetSdkProvider().Get().First(s => s.TargetFrameworkMoniker == tfm);
        var key = $"runpad-test-{Guid.NewGuid()}";
        try
        {
            var output = await RunAsync(Options(sdk),
                "Console.WriteLine(\"runtime=\" + Environment.Version);\n" +
                $"Console.WriteLine(\"cache=\" + Util.Cache(() => 5, \"{key}\") + Util.Cache(() => 6, \"{key}\"));\n" +
                "#if NET8_0_OR_GREATER\nConsole.WriteLine(\"net8+\");\n#endif\n");

            var major = tfm["net".Length..].Split('.')[0];
            Assert.Contains(output, o => o.StartsWith($"StandardOutput: runtime={major}.", StringComparison.Ordinal));
            Assert.Contains("StandardOutput: cache=55", output);
            Assert.Equal(int.Parse(major, CultureInfo.InvariantCulture) >= 8, output.Contains("StandardOutput: net8+"));
        }
        finally
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RunPad", "UserData", "Cache", key + ".json");
            if (File.Exists(cache)) File.Delete(cache);
        }
    }

    [Fact]
    public async Task SourceGenerators_FromTheTargetingPack_Should_Run()
    {
        var output = await RunAsync(Options(), """
            Console.WriteLine(Patterns.Digits().Match("abc42").Value);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new Point(1, 2), PointContext.Default.Point));

            static partial class Patterns
            {
                [System.Text.RegularExpressions.GeneratedRegex(@"\d+")]
                public static partial System.Text.RegularExpressions.Regex Digits();
            }

            record Point(int X, int Y);

            [System.Text.Json.Serialization.JsonSerializable(typeof(Point))]
            partial class PointContext : System.Text.Json.Serialization.JsonSerializerContext;
            """);

        Assert.Contains("StandardOutput: 42", output);
        Assert.Contains("StandardOutput: {\"X\":1,\"Y\":2}", output);
    }

    [Fact]
    public async Task AssemblyReference_Should_BeCopiedWithItsSameFolderDependencies()
    {
        var libDir = Directory.CreateDirectory(Path.Combine(_root, "libs")).FullName;
        var inner = CompileLibrary(libDir, "Inner", "public static class Inner { public static string Name => \"inner\"; }", []);
        var outer = CompileLibrary(libDir, "Outer", "public static class Outer { public static string Hello() => \"hello \" + Inner.Name; }", [inner]);
        var options = Options(assemblyReferences: [outer]);

        var output = await RunAsync(options, "Console.WriteLine(Outer.Hello());");

        Assert.Contains("StandardOutput: hello inner", output);
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "bin", "Outer.dll")));
        Assert.True(File.Exists(Path.Combine(options.OutputDirectory, "bin", "Inner.dll")));
    }

    internal static string CompileLibrary(string dir, string name, string code, string[] references)
    {
        var refs = new CSharpReferenceService().GetDefault().Concat(references)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code)], refs,
            new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(dir, name + ".dll");
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return path;
    }

    // Project references need MSBuild to build the referenced project; RunPad no longer uses MSBuild.
    [Fact]
    public async Task ProjectReferences_Should_FailWithAnExplanation_WithoutCompiling()
    {
        var options = Options(projectReferences: [Path.Combine(_root, "lib", "Lib.csproj")]);
        using var compiler = CSharp(options);

        var result = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Project references are no longer supported: Lib.csproj", Assert.Single(result.Errors).Message);
        Assert.False(File.Exists(Path.Combine(options.OutputDirectory, "RunPadQuery.cs")));
    }
}
