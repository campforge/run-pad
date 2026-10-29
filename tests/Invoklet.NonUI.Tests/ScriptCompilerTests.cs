using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Build.Core.Models;
using Invoklet.Contracts.Models;
using Invoklet.Debug;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Languages.VB;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// Scripts compile in-process with Roslyn (CSharpScriptCompiler,
// VbScriptCompiler); Invoklet no longer uses MSBuild. Real compilations and real script processes. The
// expectations reproduce what the old `dotnet build` path gave (several of these were MSBuild-era
// regression tests, ported here).
[Trait("Category", "RealBuild")]
[Collection(RealBuildTestCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "RealBuild")]
public sealed class ScriptCompilerTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("Invoklet-script-compiler").FullName;
    private int _dirs;

    // Bounded retry for a briefly locked bin/Invoklet.Runtime.dll on Windows — see TempDirectoryCleanup.
    public ValueTask DisposeAsync() => TempDirectoryCleanup.DeleteAsync(_root);

    private static DotnetSdk LatestSdk => new DotnetSdkProvider().Get().First();

    private CompiledDotnetLanguageHostOptions Options(
        DotnetSdk? sdk = null, RpadKind kind = RpadKind.Statements, IReadOnlyList<string>? usings = null,
        IReadOnlyList<string>? assemblyReferences = null, IReadOnlyList<string>? projectReferences = null,
        string? runtimeLib = null, string? outputDirectory = null, string? argumentContractJson = null)
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
            ArgumentContractJson = argumentContractJson,
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

    [Theory]
    [InlineData(false, "#if Invoklet\nConsole.WriteLine(\"Invoklet\");\n#else\nConsole.WriteLine(\"Other\");\n#endif")]
    [InlineData(true, "#If Invoklet Then\nConsole.WriteLine(\"Invoklet\")\n#Else\nConsole.WriteLine(\"Other\")\n#End If")]
    public async Task Compilation_Should_DefineInvokletForCSharpAndVisualBasic(bool vb, string code)
    {
        var output = await RunAsync(Options(), code, vb);

        Assert.Contains(output, line => line.EndsWith(": Invoklet", StringComparison.Ordinal));
        Assert.DoesNotContain(output, line => line.EndsWith(": Other", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "var value = Arguments.Count; Console.WriteLine(value);")]
    [InlineData(true, "Dim value As Integer = Arguments.Count\nConsole.WriteLine(value)")]
    public async Task TypedUtilArgs_Should_CompileForCSharpAndVisualBasic(bool vb, string code)
    {
        var contract = new RpadArgumentContract(
            [new("count", "int", ["3"], [])]).ToJson();
        var options = Options(argumentContractJson: contract);
        using var compiler = vb ? Vb(options) : CSharp(options);

        var result = await compiler.Compilation(
            vb ? $"Module Program\nSub Main()\n{code}\nEnd Sub\nEnd Module\n" : code,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, Errors(result));
        var output = await RunAsync(options, code, vb);
        Assert.True(output.Any(line => line.EndsWith(": 3", StringComparison.Ordinal)), string.Join("\n", output));
    }

    [Fact]
    public async Task TypedUtilArgs_Should_AggregateRuntimeConversionErrors()
    {
        var contract = new RpadArgumentContract(
        [
            new("count", "int", ["not-an-int"], []),
            new("Enabled", "bool", ["maybe"], []),
        ]).ToJson();
        var output = await RunAsync(Options(argumentContractJson: contract), "var value = Arguments.Count;");

        var text = string.Join("\n", output);
        Assert.Contains("count", text, StringComparison.Ordinal);
        Assert.Contains("Enabled", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedArguments_Should_ExposeARealEnum_InCSharpAndVisualBasic(bool vb)
    {
        var fm = new RpadFrontMatter
        {
            Arguments = [new() { Name = "env", Type = RpadArgumentType.Enum, Values = ["dev", "prod"], Default = RpadValue.Of("dev") }],
            SelectedArgumentSet = "production",
            ArgumentSets = new() { ["production"] = new() { ["env"] = RpadValue.Of("prod") } },
        };
        var request = ExecutionRequest.For(fm, null);
        Assert.Empty(request.ArgumentContractErrors);
        var options = Options(argumentContractJson: request.ArgumentContractJson);
        var code = vb
            ? "Dim environment As ArgumentsEnv = Arguments.Env\nConsole.WriteLine(environment = ArgumentsEnv.Prod)\nConsole.WriteLine(environment.GetType().IsEnum)\nConsole.WriteLine(environment.ToString())"
            : "ArgumentsEnv environment = Arguments.Env; Console.WriteLine(environment == ArgumentsEnv.Prod); Console.WriteLine(environment.GetType().IsEnum); Console.WriteLine(environment.ToString());";
        using var compiler = vb ? Vb(options) : CSharp(options);
        var result = await compiler.Compilation(vb ? $"Module Program\nSub Main()\n{code}\nEnd Sub\nEnd Module\n" : code,
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, Errors(result));
        Assert.Empty(result.Warnings);
        var output = await RunAsync(options, code, vb);
        Assert.Equal(2, output.Count(line => line.EndsWith(": True", StringComparison.Ordinal)));
        Assert.Contains(output, line => line.EndsWith(": Prod", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedArguments_Should_KeepAnUnsetEnumNullable(bool vb)
    {
        var fm = new RpadFrontMatter
        {
            Arguments = [new() { Name = "env", Type = RpadArgumentType.Enum, Values = ["dev", "prod"] }],
        };
        var contract = RpadArgumentContract.Build(fm, out var errors);
        Assert.Empty(errors);
        var code = vb
            ? "Dim environment As ArgumentsEnv? = Arguments.Env\nConsole.WriteLine(environment.HasValue)"
            : "ArgumentsEnv? environment = Arguments.Env; Console.WriteLine(environment.HasValue);";
        var output = await RunAsync(Options(argumentContractJson: contract.ToJson()), code, vb);
        Assert.Contains(output, line => line.EndsWith(": False", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedArguments_Should_MapEnumLabelsToSafeIdentifiers(bool vb)
    {
        var contract = new RpadArgumentContract([new("env", "enum", ["prod-us"], ["dev", "prod-us", "true"])]);
        var code = vb
            ? "Console.WriteLine(Arguments.Env = ArgumentsEnv.ProdUs)\nConsole.WriteLine(CInt(ArgumentsEnv.[True]))"
            : "Console.WriteLine(Arguments.Env == ArgumentsEnv.ProdUs); Console.WriteLine((int)ArgumentsEnv.True);";
        var output = await RunAsync(Options(argumentContractJson: contract.ToJson()), code, vb);
        Assert.Contains(output, line => line.EndsWith(": True", StringComparison.Ordinal));
        Assert.Contains(output, line => line.EndsWith(": 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedArguments_Should_RejectUnknownEnumValuesAtRuntime()
    {
        var contract = new RpadArgumentContract([new("env", "enum", ["unknown"], ["dev", "prod"])]);
        var output = await RunAsync(Options(argumentContractJson: contract.ToJson()), "Console.WriteLine(Arguments.Env);");
        var message = string.Join("\n", output);
        Assert.Contains("env", message);
        Assert.Contains("unknown", message);
        Assert.Contains("dev, prod", message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TypedArguments_Should_RunTheCapturedRequestValues(bool vb, bool empty)
    {
        var fm = new RpadFrontMatter
        {
            Arguments = [new() { Name = "value", Type = RpadArgumentType.Int }],
            SelectedArgumentSet = "old",
            ArgumentSets = new()
            {
                ["old"] = new() { ["value"] = RpadValue.Of("99") },
                ["selected"] = empty ? new() : new() { ["value"] = RpadValue.Of("3") },
            },
        };
        var request = ExecutionRequest.For(fm, null, argumentSet: "selected");
        var options = CompiledDotnetLanguageHostOptions.From(request with
        {
            Platform = LatestSdk,
            OutputDirectory = Directory.CreateDirectory(Path.Combine(_root, $"out{++_dirs}")).FullName,
        }, new CSharpReferenceService().GetExtensionReference());
        var code = vb
            ? "Console.WriteLine(If(Arguments.Value.HasValue, Arguments.Value.Value.ToString(), \"missing\"))"
            : "Console.WriteLine(Arguments.Value.HasValue ? Arguments.Value.Value.ToString() : \"missing\");";
        var output = await RunAsync(options, code, vb);
        Assert.True(output.Any(line => line.EndsWith(empty ? ": missing" : ": 3", StringComparison.Ordinal)), string.Join("\n", output));
    }

    [Fact]
    public async Task TypedArguments_Should_ChangeOnRerun_WhenPresetChanges()
    {
        var fm = new RpadFrontMatter
        {
            Arguments = [new() { Name = "value", Type = RpadArgumentType.Int }],
            ArgumentSets = new()
            {
                ["dev"] = new() { ["value"] = RpadValue.Of("3") },
                ["prod"] = new() { ["value"] = RpadValue.Of("7") },
            },
        };
        var directory = Path.Combine(_root, "preset-rerun");
        foreach (var (preset, expected) in new[] { ("dev", "3"), ("prod", "7"), ("dev", "3") })
        {
            fm.SelectedArgumentSet = preset;
            var request = ExecutionRequest.For(fm, null);
            var options = Options(outputDirectory: directory, argumentContractJson: request.ArgumentContractJson);
            var output = await RunAsync(options, "Console.WriteLine(Arguments.Value);");
            Assert.True(output.Any(line => line.EndsWith(": " + expected, StringComparison.Ordinal)), string.Join("\n", output));
        }
    }

    [Fact]
    public async Task TypedArguments_Should_RecompileWhenTheDeclaredTypeChanges()
    {
        var directory = Path.Combine(_root, "argument-types");
        foreach (var (type, value, expected) in new[] { ("int", "3", "Int32"), ("string", "text", "String") })
        {
            var contract = new RpadArgumentContract([new("value", type, [value], [])]);
            var options = Options(outputDirectory: directory, argumentContractJson: contract.ToJson());
            var output = await RunAsync(options, "Console.WriteLine(Arguments.Value.GetType().Name);");
            Assert.True(output.Any(line => line.EndsWith(": " + expected, StringComparison.Ordinal)), string.Join("\n", output));
        }
    }

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
        var sourcePath = Path.Combine(dir, "InvokletQuery.cs");
        var pdbPath = Path.Combine(dir, "bin", "InvokletQuery.pdb");
        Assert.Equal(Path.Combine(dir, "bin", "InvokletQuery.dll"), result.OutputBinaryFile);
        Assert.Equal(code, File.ReadAllText(sourcePath));
        Assert.True(File.Exists(Path.Combine(dir, "bin", "Invoklet.Runtime.dll")));
        Assert.Contains($"\"tfm\": \"{options.Platform.TargetFrameworkMoniker}\"",
            File.ReadAllText(Path.Combine(dir, "bin", "InvokletQuery.runtimeconfig.json")));

        // netcoredbg pairs the DLL with its PDB through the CodeView entry, and maps breakpoints set on
        // OutputDirectory/InvokletQuery.cs through the PDB's document table; the script is embedded.
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
        var script = "InvokletQuery" + (vb ? ".vb" : ".cs");
        var breakpointSource = DebugSourcePath.Canonicalize(Path.Combine(alias, script));
        Assert.Equal(DebugSourcePath.Canonicalize(Path.Combine(real, script)), breakpointSource);
        var documents = PdbDocuments(Path.Combine(alias, "bin", "InvokletQuery.pdb"));
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
        Assert.Equal(Path.DirectorySeparatorChar + "InvokletQuery.cs", Assert.Single(result.Errors).File);
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
        var pdbPath = Path.Combine(alias, "bin", "InvokletQuery.pdb");
        Assert.Contains((DebugSourcePath.Canonicalize(Path.Combine(real, "InvokletQuery.cs")), true), PdbDocuments(pdbPath));

        using (var compiler = CSharp(options))
            Assert.True((await compiler.Compilation(code, TestContext.Current.CancellationToken)).Success);

        var documents = PdbDocuments(pdbPath);
        Assert.Contains((DebugSourcePath.Canonicalize(Path.Combine(moved, "InvokletQuery.cs")), true), documents);
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
            var file = Path.DirectorySeparatorChar + "InvokletQuery.cs";
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

    // Updating Invoklet rebuilds Invoklet.Runtime.dll in place (same path): the script must be rebuilt
    // against it rather than keep running against the old copy in bin/.
    [Fact]
    public async Task ARebuiltRuntimeLibrary_Should_ForceARebuild_EvenIfTheScriptDidntChange()
    {
        var libDir = Directory.CreateDirectory(Path.Combine(_root, "runtime-lib")).FullName;
        var runtimeLib = Path.Combine(libDir, "Invoklet.Runtime.dll");
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
    public async Task CSharp_Should_SeeInvokletRuntimeTypes_WithoutAUsing()
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

    // vbc settings of the SDK: Invoklet.Runtime + SDK default imports, Option Strict Custom (implicit
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

        Assert.Contains("StandardOutput: InvokletQuery.M 4 6 hello", output);
        Assert.Contains("StandardOutput: defines ok", output);
    }

    [Fact]
    public async Task ProgramMain_GuardedByATargetFrameworkSymbol_Should_Execute()
    {
        var output = await RunAsync(Options(kind: RpadKind.Program), """
            #if NET8_0_OR_GREATER
            static void Main() => Console.WriteLine("main ran");
            #endif
            """);

        Assert.Contains("StandardOutput: main ran", output);
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
    // .NET Invoklet itself runs on.
    [Theory]
    [MemberData(nameof(InstalledTargetFrameworks))]
    public async Task Script_Should_CompileAgainstAndRunOnItsTargetFramework(string tfm)
    {
        var sdk = new DotnetSdkProvider().Get().First(s => s.TargetFrameworkMoniker == tfm);
        var key = $"Invoklet-test-{Guid.NewGuid()}";
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
                "Invoklet", "UserData", "Cache", key + ".json");
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

    // Project references need MSBuild to build the referenced project; Invoklet no longer uses MSBuild.
    [Fact]
    public async Task ProjectReferences_Should_FailWithAnExplanation_WithoutCompiling()
    {
        var options = Options(projectReferences: [Path.Combine(_root, "lib", "Lib.csproj")]);
        using var compiler = CSharp(options);

        var result = await compiler.Compilation("Console.WriteLine(1);", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Project references are no longer supported: Lib.csproj", Assert.Single(result.Errors).Message);
        Assert.False(File.Exists(Path.Combine(options.OutputDirectory, "InvokletQuery.cs")));
    }
}
