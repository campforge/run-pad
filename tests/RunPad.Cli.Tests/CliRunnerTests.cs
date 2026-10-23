using RunPad.Cli;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using Spectre.Console.Testing;
using Xunit;

namespace RunPad.Cli.Tests;

// BuildOptions is the one piece of CliRunner.RunAsync that's pure logic (no process
// spawning, no real SDK needed) — mirrors EditorTabItemControl.BuildRunOptions for the GUI's own
// non-debug run path, so these pin down that the two stay equivalent.
public class CliRunnerTests
{
    private static readonly DotnetSdk Platform = new("net10.0", "10.0.100", ".NET 10.0.100", ".NET 10");
    private const string RuntimeLib = "/runpad/RunPad.Runtime.dll";

    // The options the runner's request gets from the .NET backend: what its run is built with.
    private static ILanguageHostOptions BuildOptions(
        RpadFrontMatter frontMatter, DotnetSdk platform, string outputDirectory, string runtimeLibPath,
        string? currentQueryPath = null, IReadOnlyList<string>? arguments = null)
        => CompiledDotnetLanguageHostOptions.From(
            CliRunner.BuildRequest(frontMatter, platform, outputDirectory, currentQueryPath, arguments), runtimeLibPath);

    private static RpadFrontMatter CreateFrontMatter(RpadLanguage language = RpadLanguage.CSharp) => new()
    {
        Language = language,
        Kind = RpadKind.Program,
        Packages = new Dictionary<string, Package> { ["Newtonsoft.Json"] = new("13.0.3") },
        Namespaces = ["System.Text"],
        References = ["Some.Native.dll", "Some.Project.csproj", "Some.Vb.vbproj"],
    };

    // One options type for every compiled .NET language — CSharp/VB used to
    // each get their own byte-for-byte identical options class, so these two tests used to assert
    // different Assert.IsType<> targets for otherwise-identical content; now there's only one type
    // to build regardless of language.
    [Fact]
    public void BuildOptions_Should_ReturnExpectedOptions_ForCSharp()
    {
        var options = BuildOptions(CreateFrontMatter(), Platform, "/tmp/out", RuntimeLib);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(Platform, csharpOptions.Platform);
        Assert.Equal("/tmp/out", csharpOptions.OutputDirectory);
        Assert.Equal(RpadKind.Program, csharpOptions.Kind);
        Assert.Equal("13.0.3", csharpOptions.Packages["Newtonsoft.Json"]);
        Assert.Equal(["System.Text"], csharpOptions.Usings);
        // No script path: a relative reference has no folder to be relative to.
        Assert.Empty(csharpOptions.AssemblyReferences);
        Assert.Contains("'Some.Native.dll' is relative to the script's folder: save the script first",
            Assert.Single(csharpOptions.ReferenceErrors));
        Assert.Equal(["Some.Project.csproj", "Some.Vb.vbproj"], csharpOptions.ProjectReferences);
        Assert.Equal(RuntimeLib, csharpOptions.RuntimeLibPath);
    }

    [Fact]
    public void BuildOptions_Should_ReturnExpectedOptions_ForVb()
    {
        var options = BuildOptions(CreateFrontMatter(RpadLanguage.Vb), Platform, "/tmp/out", RuntimeLib);

        var vbOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(Platform, vbOptions.Platform);
        Assert.Equal(RpadKind.Program, vbOptions.Kind);
        Assert.Equal(RuntimeLib, vbOptions.RuntimeLibPath);
    }

    // The runner finds SDKs, languages and RunPad.Runtime through the services it is given, so it
    // runs nothing a test (or another bootstrap) didn't hand it.
    [Fact]
    public async Task RunAsync_Should_UseTheGivenSdkProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runpad-cli-services-{Guid.NewGuid():N}.rpad");
        try
        {
            await File.WriteAllTextAsync(path, "---\nlanguage: csharp\n---\n\n```csharp run\nConsole.WriteLine(1);\n```\n");
            var sdks = new FakeSdkProvider();
            var services = CliServices.CreateDefault() with { Sdks = sdks };
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync([path], services, new TestConsole(), stderr, CancellationToken.None);

            Assert.Equal(CliRunner.NotRunExitCode, exitCode);
            Assert.Equal(1, sdks.Calls);
            Assert.Contains("No .NET SDK found", stderr.Output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RunAsync_Should_RunOnlyTheGivenLanguages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runpad-cli-services-{Guid.NewGuid():N}.rpad");
        try
        {
            await File.WriteAllTextAsync(path, "---\nlanguage: vb\n---\n\n```vb run\nConsole.WriteLine(1)\n```\n");
            var sdks = new FakeSdkProvider();
            var services = new CliServices(new LanguageHostRegistry(), sdks);
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync([path], services, new TestConsole(), stderr, CancellationToken.None);

            Assert.Equal(CliRunner.NotRunExitCode, exitCode);
            Assert.Contains("No runner registered for language 'vb'", stderr.Output);
            Assert.Equal(0, sdks.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CreateDefault_Should_RegisterCSharpAndVb()
    {
        var services = CliServices.CreateDefault();

        Assert.Equal(["csharp", "vb"], services.Registry.SupportedLanguages);
    }

    // The runner's request is the window's: the shared front-matter mapping, built in the temporary
    // build folder, with the PATH's dotnet and no debugger; command-line arguments replace args:.
    [Fact]
    public void BuildRequest_Should_CarryTheScriptsMetadata_AsWritten()
    {
        var frontMatter = CreateFrontMatter();
        frontMatter.Args = ["from", "file"];

        var request = CliRunner.BuildRequest(frontMatter, Platform, "/tmp/out", "/scripts/a.rpad", ["cli"]);

        Assert.Same(Platform, request.Platform);
        Assert.Equal("/tmp/out", request.OutputDirectory);
        Assert.Equal("/tmp/out", request.WorkingDirectory);
        Assert.Equal("/scripts/a.rpad", request.CurrentQueryPath);
        Assert.Equal(RpadKind.Program, request.Kind);
        Assert.Equal("13.0.3", request.Packages["Newtonsoft.Json"]);
        Assert.Equal(["System.Text"], request.Usings);
        Assert.Equal(["Some.Native.dll", "Some.Project.csproj", "Some.Vb.vbproj"], request.References);
        Assert.Equal(["cli"], request.Arguments);
        Assert.Null(request.RuntimePath);
        Assert.Null(request.DebuggerPath);
    }

    // The runner only builds a plain ExecutionRequest: no code in it builds the .NET backend's host
    // options or resolves references (CliServices registers the concrete languages, nothing more).
    [Fact]
    public void TheRunner_Should_NotBuildTheBackendsHostOptions()
    {
        string[] backendTypes =
        [
            "RunPad.Languages.DotnetCommon.CompiledDotnetLanguageHostOptions",
            "RunPad.Languages.DotnetCommon.ReferencePathResolver",
            "RunPad.Languages.DotnetCommon.RunPadNuGetConfigStore",
            "RunPad.Build.Core.Models.ExecutionOptions",
            "RunPad.Build.Core.Models.RuntimeOptions",
            "RunPad.Build.Core.Models.DebuggerOptions",
        ];
        using var stream = File.OpenRead(typeof(CliRunner).Assembly.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var referenced = metadata.TypeReferences
            .Select(h => metadata.GetTypeReference(h))
            .Select(t => metadata.GetString(t.Namespace) + "." + metadata.GetString(t.Name));

        Assert.DoesNotContain(referenced, backendTypes.Contains);
    }

    // --refresh-connections reads every connection's description again before the build; without it
    // one already read is used as it is. The build itself is never reached: the language host
    // stops the run as soon as it is asked for.
    [Fact]
    public async Task RefreshConnections_Should_ReadAnAlreadyReadDescriptionAgain()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-cli-refresh").FullName;
        try
        {
            var path = Path.Combine(folder, "a.rpad");
            await File.WriteAllTextAsync(path, "---\nlanguage: csharp\nconnections:\n  - name: stock\n    driver: counting\n"
                + "    params:\n      address: http://localhost:5000\n---\n\n```csharp run\n1.Dump();\n```\n");
            var driver = new CountingSchemaDriver();
            var drivers = new ConnectionDriverRegistry();
            drivers.Register(driver);
            var languages = new LanguageHostRegistry();
            languages.Register(new StoppingLanguage());
            var schemas = new ConnectionSchemas(drivers, Path.Combine(folder, "schemas"),
                new ConnectionTools { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") }, _ => null);
            var services = new CliServices(languages, new OneSdkProvider(), drivers, schemas);

            async Task Run(params string[] options) => await Assert.ThrowsAsync<StoppedBeforeTheBuild>(() =>
                CliRunner.RunAsync([.. options, path], services, new TestConsole(), new TestConsole(), CancellationToken.None));
            await Run();
            await Run();
            Assert.Equal(1, driver.Fetches);
            await Run("--refresh-connections");
            Assert.Equal(2, driver.Fetches);
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class CountingSchemaDriver : IConnectionSchemaDriver
    {
        public int Fetches;
        public string Id => "counting";
        public string DisplayName => "Counting";
        public ConnectionCategory Category => ConnectionCategory.Grpc;
        public IReadOnlyList<ConnectionParam> Params => [];
        public IReadOnlyDictionary<string, string> Packages => new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(RpadConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) => new("");
        public Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Fetches);
            return Task.FromResult(new ConnectionSchema("1 service", [], ""));
        }
    }

    private sealed class StoppedBeforeTheBuild : Exception;

    private sealed class StoppingLanguage : ILanguageHostFactory
    {
        public string Language => "csharp";
        public string DisplayName => "C#";
        public LanguageHostCapabilities Capabilities => default;
        public IReadOnlyDictionary<string, string> Snippets => new Dictionary<string, string>();
        public EditorLanguageProfile EditorProfile => new();
        public ILanguageHost Create(ILanguageHostOptions? options = null) => throw new StoppedBeforeTheBuild();
        public ILanguageHost Create(ExecutionRequest request) => throw new StoppedBeforeTheBuild();
    }

    private sealed class OneSdkProvider : ISdkProvider
    {
        public IReadOnlyList<DotnetSdk> Get(string? dotnetExecutableOverride = null) => [Platform];
    }

    private sealed class FakeSdkProvider : ISdkProvider
    {
        public int Calls { get; private set; }

        public IReadOnlyList<DotnetSdk> Get(string? dotnetExecutableOverride = null)
        {
            Calls++;
            return [];
        }
    }

    [Fact]
    public void BuildOptions_Should_DefaultToEmptyCollections_WhenFrontMatterHasNone()
    {
        var frontMatter = new RpadFrontMatter(); // Namespaces/References both null by default

        var options = BuildOptions(frontMatter, Platform, "/tmp/out", RuntimeLib);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Empty(csharpOptions.Usings);
        Assert.Empty(csharpOptions.AssemblyReferences);
        Assert.Empty(csharpOptions.ProjectReferences);
    }

    // $(CurrentQueryPath) resolves against the currentQueryPath parameter threaded in
    // from RunAsync's own filePath — mirrors EditorTabItemControl.BuildRunOptions's use of
    // _vm.FilePath for the same macro (see ReferencePathMacros.Expand).
    [Fact]
    public void BuildOptions_Should_ExpandCurrentQueryPathMacro_InReferences()
    {
        var dir = Directory.CreateTempSubdirectory("runpad-cli-references").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Lib.dll"), "");
            var currentQueryPath = Path.Combine(dir, "query.rpad");
            var frontMatter = new RpadFrontMatter
            {
                References = ["$(CurrentQueryPath)/Lib.dll", "$(CurrentQueryPath)/Shared.csproj"],
            };

            var options = BuildOptions(frontMatter, Platform, "/tmp/out", RuntimeLib, currentQueryPath);

            var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
            Assert.Equal([Path.Combine(dir, "Lib.dll")], csharpOptions.AssemblyReferences);
            Assert.Equal([dir + "/Shared.csproj"], csharpOptions.ProjectReferences);
            Assert.Empty(csharpOptions.ReferenceErrors);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Untitled: $(CurrentQueryPath) has nothing to stand for — reported, not passed on unexpanded.
    [Fact]
    public void BuildOptions_Should_ReportTheCurrentQueryPathMacro_WhenNoFilePathGiven()
    {
        var frontMatter = new RpadFrontMatter { References = ["$(CurrentQueryPath)/Lib.dll"] };

        var options = BuildOptions(frontMatter, Platform, "/tmp/out", RuntimeLib);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Empty(csharpOptions.AssemblyReferences);
        Assert.Contains("uses $(CurrentQueryPath), the script's folder: save the script first", Assert.Single(csharpOptions.ReferenceErrors));
    }

    // Distinct from the $(CurrentQueryPath)
    // reference-macro tests above: this is the options OBJECT's own CurrentQueryPath property (what
    // actually reaches the compiled script's process as an environment variable), not just a string
    // substitution in References.
    [Fact]
    public void BuildOptions_Should_SetCurrentQueryPath_OnTheOptionsObject()
    {
        var currentQueryPath = Path.Combine("home", "user", "scripts", "query.rpad");

        var options = BuildOptions(CreateFrontMatter(), Platform, "/tmp/out", RuntimeLib, currentQueryPath);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(currentQueryPath, csharpOptions.CurrentQueryPath);
    }

    [Fact]
    public void BuildOptions_Should_LeaveCurrentQueryPathNull_WhenNoFilePathGiven()
    {
        var options = BuildOptions(CreateFrontMatter(), Platform, "/tmp/out", RuntimeLib);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Null(csharpOptions.CurrentQueryPath);
    }

    [Fact]
    public void BuildOptions_Should_ExpandRuntimeDirectoryAndMyDocumentsMacros_InReferences()
    {
        var frontMatter = new RpadFrontMatter { References = ["$(RuntimeDirectory)/System.dll"] };

        var options = BuildOptions(frontMatter, Platform, "/tmp/out", RuntimeLib);

        // A resolved reference is a full path in the platform's own spelling ('\\' on Windows): the
        // stored "/" separator is not what the build receives.
        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        var expected = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.dll"));
        Assert.Equal([expected], csharpOptions.AssemblyReferences);
        Assert.Empty(csharpOptions.ReferenceErrors);
    }

    [Fact]
    public async Task RunAsync_Should_ReturnError_WhenNoArgsGiven()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync([], stdout, stderr, CancellationToken.None);

        Assert.Equal(CliRunner.NotRunExitCode, exitCode);
        Assert.Contains("Usage:", stderr.Output);
    }

    [Fact]
    public async Task RunAsync_Should_ReturnError_WhenFileDoesNotExist()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync(
            ["/no/such/file.rpad"], stdout, stderr, CancellationToken.None);

        Assert.Equal(CliRunner.NotRunExitCode, exitCode);
        Assert.Contains("File not found", stderr.Output);
    }

    [Fact]
    public async Task RunAsync_Should_ReturnError_WhenFileIsNotAValidRpadDocument()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "not a .rpad document at all");
            var stdout = new TestConsole();
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(
                [path], stdout, stderr, CancellationToken.None);

            Assert.Equal(CliRunner.NotRunExitCode, exitCode);
            Assert.Contains("Failed to parse", stderr.Output);
        }
        finally { File.Delete(path); }
    }

    // Malformed YAML used to silently become a default C# document and run (or fail on)
    // the wrong code — now rejected before any SDK lookup/compilation, with the YAML diagnostic.
    [Fact]
    public async Task RunAsync_Should_RejectInvalidFrontMatter_WithDiagnostic()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path,
                "---\nlanguage: vb\nkind: nonsense\npackages:\n  Newtonsoft.Json: 13.0.3\n---\n\n```vb run\nConsole.WriteLine(1)\n```\n");
            var stdout = new TestConsole();
            // Wide enough that the diagnostic after the long temp path isn't wrapped mid-phrase.
            var stderr = new TestConsole().Width(1000);

            var exitCode = await CliRunner.RunAsync(
                [path], stdout, stderr, CancellationToken.None);

            Assert.Equal(CliRunner.NotRunExitCode, exitCode);
            Assert.Contains("Failed to parse", stderr.Output);
            Assert.Contains("Invalid YAML front matter (line 3)", stderr.Output);
            Assert.Contains("nonsense", stderr.Output);
        }
        finally { File.Delete(path); }
    }

    // `kind: 99` used to parse as an undefined RpadKind and run.
    [Theory]
    [InlineData("kind: 99", "Unsupported kind '99'")]
    [InlineData("kind: Unknown", "Unsupported kind 'Unknown'")]
    [InlineData("language: 0", "Unsupported language '0'")]
    public async Task RunAsync_Should_RejectUnsupportedEnumValues_WithDiagnostic(string line, string expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, $"---\n{line}\n---\n\n```csharp run\nConsole.WriteLine(1);\n```\n");
            var stdout = new TestConsole();
            var stderr = new TestConsole().Width(1000);

            var exitCode = await CliRunner.RunAsync(
                [path], stdout, stderr, CancellationToken.None);

            Assert.Equal(CliRunner.NotRunExitCode, exitCode);
            Assert.Contains("Failed to parse", stderr.Output);
            Assert.Contains(expected, stderr.Output);
        }
        finally { File.Delete(path); }
    }

    // ── exit code ────────────────────────────────────────────────────

    [Theory]
    [InlineData(RunOutcome.Exited, 0, 0)]
    [InlineData(RunOutcome.Exited, 7, 7)]
    [InlineData(RunOutcome.Exited, -1, -1)]
    [InlineData(RunOutcome.CompilationFailed, null, 2)]
    [InlineData(RunOutcome.StartFailed, null, 2)]
    public void ExitCodeFor_Should_BeTheScriptsOwnExitCode_Or2WhenItNeverRan(RunOutcome outcome, int? exitCode, int expected)
    {
        Assert.Equal(expected, CliRunner.ExitCodeFor(new RunResult(outcome, exitCode)));
    }

    // Real compile + real script process (needs an installed .NET SDK, like the GUI's own run).
    [Theory]
    [Trait("Category", "RealBuild")]
    [InlineData("Console.WriteLine(\"done\");\nEnvironment.Exit(7);", 7)]
    [InlineData("Console.Error.WriteLine(\"just a warning\");", 0)]
    [InlineData("int x = \"not an int\";", 2)]
    public async Task RunAsync_Should_ReturnTheScriptsExitCode(string code, int expected)
    {
        if (!new DotnetSdkProvider().Get().Any()) return; // no .NET SDK to compile with
        var path = Path.Combine(Path.GetTempPath(), $"runpad-cli-exit-{Guid.NewGuid():N}.rpad");
        try
        {
            await File.WriteAllTextAsync(path, $"---\nlanguage: csharp\nkind: Statements\n---\n\n```csharp run\n{code}\n```\n");
            var stdout = new TestConsole();
            var stderr = new TestConsole().Width(1000);

            var exitCode = await CliRunner.RunAsync([path], stdout, stderr, CancellationToken.None);

            Assert.True(expected == exitCode, $"exit code {exitCode}\nstdout:\n{stdout.Output}\nstderr:\n{stderr.Output}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BuildOptions_Should_UseTheFrontMatterArgs_UnlessTheCommandLineGivesSome()
    {
        var frontMatter = CreateFrontMatter();
        frontMatter.Args = ["from", "file"];

        var fromFile = Assert.IsType<CompiledDotnetLanguageHostOptions>(
            BuildOptions(frontMatter, Platform, Path.GetTempPath(), RuntimeLib));
        var fromCommandLine = Assert.IsType<CompiledDotnetLanguageHostOptions>(
            BuildOptions(frontMatter, Platform, Path.GetTempPath(), RuntimeLib, arguments: ["cli"]));

        Assert.Equal(["from", "file"], fromFile.Arguments);
        Assert.Equal(["cli"], fromCommandLine.Arguments);
    }

    // Real compile + real script process: args reach C# top-level statements, a Program's
    // Main(string[] args) and Visual Basic scripts.
    [Theory]
    [Trait("Category", "RealBuild")]
    [InlineData("csharp", "Statements", "Console.WriteLine(string.Join(\"|\", args));")]
    [InlineData("csharp", "Program", "void Main(string[] args) => Console.WriteLine(string.Join(\"|\", args));")]
    [InlineData("vb", "Statements", "Console.WriteLine(String.Join(\"|\", args))")]
    [InlineData("vb", "Program", "Module Program\nSub Main(args As String())\nConsole.WriteLine(String.Join(\"|\", args))\nEnd Sub\nEnd Module")]
    public async Task RunAsync_Should_PassTheArguments_ToTheScript(string language, string kind, string code)
    {
        if (!new DotnetSdkProvider().Get().Any()) return; // no .NET SDK to compile with
        var path = Path.Combine(Path.GetTempPath(), $"runpad-cli-args-{Guid.NewGuid():N}.rpad");
        try
        {
            await File.WriteAllTextAsync(path,
                $"---\nlanguage: {language}\nkind: {kind}\nargs:\n  - \"from file\"\n---\n\n```{language} run\n{code}\n```\n");

            var fromFile = new TestConsole();
            var fromCommandLine = new TestConsole();
            var stderr = new TestConsole().Width(1000);

            var fileExit = await CliRunner.RunAsync([path], fromFile, stderr, CancellationToken.None);
            var cliExit = await CliRunner.RunAsync(["run", path, "a b", "--", "-x"], fromCommandLine, stderr, CancellationToken.None);

            Assert.True(fileExit == 0 && cliExit == 0, $"exit codes {fileExit}/{cliExit}\nstderr:\n{stderr.Output}");
            Assert.Equal("from file", fromFile.Output.Trim());
            Assert.Equal("a b|-x", fromCommandLine.Output.Trim());
        }
        finally { File.Delete(path); }
    }

    // lprun's -lang and npad's --kind/--sdk override the front matter; -compileonly never runs it.
    [Theory]
    [Trait("Category", "RealBuild")]
    [InlineData(new[] { "-lang=e" }, "6", 0)]
    [InlineData(new[] { "--kind", "expression", "--sdk", "latest" }, "6", 0)]
    [InlineData(new[] { "-lang=e", "-compileonly" }, "", 0)]
    [InlineData(new[] { "-lang=VBExpression" }, "", 2)]
    public async Task RunAsync_Should_ApplyTheOptions(string[] options, string expectedOutput, int expectedExit)
    {
        if (!new DotnetSdkProvider().Get().Any()) return; // no .NET SDK to compile with
        var path = Path.Combine(Path.GetTempPath(), $"runpad-cli-options-{Guid.NewGuid():N}.rpad");
        try
        {
            // As statements this doesn't compile (CS0201), as an expression it dumps 6.
            await File.WriteAllTextAsync(path, "---\nlanguage: csharp\nkind: Statements\n---\n\n```csharp run\n1 + 5\n```\n");
            var stdout = new TestConsole();
            var stderr = new TestConsole().Width(1000);

            var exitCode = await CliRunner.RunAsync([.. options, path], stdout, stderr, CancellationToken.None);

            Assert.True(expectedExit == exitCode, $"exit code {exitCode}\nstdout:\n{stdout.Output}\nstderr:\n{stderr.Output}");
            // An expression's dump is titled with the expression; the value is the last line.
            Assert.Equal(expectedOutput, stdout.Output.Trim().Split('\n')[^1]);
        }
        finally { File.Delete(path); }
    }

    // A missing or unreadable SDK value is a command-line error: nothing compiles or runs. A valid SDK
    // that isn't installed still runs, on the newest SDK, with a warning.
    [Theory]
    [Trait("Category", "RealBuild")]
    [InlineData(new[] { "--sdk", "--compile-only" }, "The option 'sdk' needs a value.")]
    [InlineData(new[] { "-s", "-compileonly" }, "The option 's' needs a value.")]
    [InlineData(new[] { "--sdk", "banana" }, "Unsupported SDK 'banana'.")]
    [InlineData(new[] { "-s=" }, "The option 's' needs a value.")]
    [InlineData(new[] { "--sdk", "1" }, null)]
    public async Task RunAsync_Should_RunTheScript_OnlyForAValidSdk(string[] options, string? error)
    {
        if (!new DotnetSdkProvider().Get().Any()) return; // no .NET SDK to compile with
        var path = Path.Combine(Path.GetTempPath(), $"runpad-cli-sdk-{Guid.NewGuid():N}.rpad");
        try
        {
            await File.WriteAllTextAsync(path, "---\nlanguage: csharp\nkind: Statements\n---\n\n```csharp run\nConsole.WriteLine(\"SCRIPT_EXECUTED\");\n```\n");
            var stdout = new TestConsole();
            var stderr = new TestConsole().Width(1000);

            var exitCode = await CliRunner.RunAsync([.. options, path], stdout, stderr, CancellationToken.None);

            var log = $"exit code {exitCode}\nstdout:\n{stdout.Output}\nstderr:\n{stderr.Output}";
            if (error is null)
            {
                Assert.True(exitCode == 0, log);
                Assert.Contains("SCRIPT_EXECUTED", stdout.Output);
                Assert.Contains("targetSdk net1.0 is not installed", stderr.Output);
            }
            else
            {
                Assert.True(exitCode == CliRunner.NotRunExitCode, log);
                Assert.DoesNotContain("SCRIPT_EXECUTED", stdout.Output);
                Assert.Contains(error, stderr.Output);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task RunAsync_Should_ReportCompileErrors_WithCompileOnly()
    {
        if (!new DotnetSdkProvider().Get().Any()) return; // no .NET SDK to compile with
        var path = Path.Combine(Path.GetTempPath(), $"runpad-cli-compile-{Guid.NewGuid():N}.rpad");
        try
        {
            await File.WriteAllTextAsync(path, "---\nlanguage: csharp\n---\n\n```csharp run\nConsole.WriteLine(\"ran\");\nint x = \"no\";\n```\n");
            var stdout = new TestConsole();
            var stderr = new TestConsole().Width(1000);

            var exitCode = await CliRunner.RunAsync([path, "-compileonly"], stdout, stderr, CancellationToken.None);

            Assert.Equal(CliRunner.NotRunExitCode, exitCode);
            Assert.Contains("CS0029", stderr.Output);
            Assert.DoesNotContain("ran", stdout.Output);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task RunAsync_Should_PrintUsage_ForHelp(string option)
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync([option], stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: runpad", stdout.Output);
        // Runner options are read wherever they appear until "--", even after the script.
        Assert.Contains("until --", stdout.Output);
    }

    [Theory]
    [InlineData("-format=html")]
    [InlineData("--eval")]
    [InlineData("--bogus")]
    public async Task RunAsync_Should_RejectOptions_ItDoesNotSupport(string option)
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync([option, "a.rpad"], stdout, stderr, CancellationToken.None);

        Assert.Equal(CliRunner.NotRunExitCode, exitCode);
        Assert.NotEmpty(stderr.Output);
    }

    // ── convert ────────────────────────────────────────────────────

    private const string ValidLinq = """
        <Query Kind="Statements">
          <NuGetReference>Newtonsoft.Json</NuGetReference>
        </Query>

        Console.WriteLine("hi");
        """;

    [Fact]
    public async Task ConvertLinqAsync_Should_ReturnError_WhenNoInputPathGiven()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync(["convert"], stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage:", stderr.Output);
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_ReturnError_WhenTheInputFileDoesNotExist()
    {
        var stdout = new TestConsole();
        var stderr = new TestConsole();

        var exitCode = await CliRunner.RunAsync(
            ["convert", "/no/such/file.linq"], stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("File not found", stderr.Output);
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_WriteASiblingRpad_WhenNoOutputPathGiven()
    {
        var dir = Directory.CreateTempSubdirectory("runpad-cli-convert-test").FullName;
        try
        {
            var linqPath = Path.Combine(dir, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, ValidLinq, CancellationToken.None);
            // Wide enough that Spectre's line-wrapping never splits the "Wrote <path>" message —
            // TestConsole defaults to an 80-column width, and the machine's temp directory (which
            // varies by username/profile) can easily push that single unbroken path past 80 chars,
            // making the plain Assert.Contains below fail non-deterministically depending on machine.
            var stdout = new TestConsole();
            stdout.Profile.Width = 4096;
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(["convert", linqPath], stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            var expectedOutputPath = Path.Combine(dir, "MyQuery.rpad");
            Assert.True(File.Exists(expectedOutputPath));
            Assert.Contains(expectedOutputPath, stdout.Output);

            var parsed = RpadParser.TryParse(await File.ReadAllTextAsync(expectedOutputPath, CancellationToken.None));
            Assert.NotNull(parsed);
            Assert.Equal(RpadKind.Statements, parsed!.FrontMatter.Kind);
            Assert.Contains("Newtonsoft.Json", parsed.FrontMatter.Packages.Keys);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_WriteToTheGivenOutputPath_AndOverwriteIt()
    {
        var dir = Directory.CreateTempSubdirectory("runpad-cli-convert-test").FullName;
        try
        {
            var linqPath = Path.Combine(dir, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, ValidLinq, CancellationToken.None);
            var outputPath = Path.Combine(dir, "Somewhere.rpad");
            await File.WriteAllTextAsync(outputPath, "will be overwritten", CancellationToken.None);
            var stdout = new TestConsole();
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(
                ["convert", linqPath, outputPath], stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            var written = await File.ReadAllTextAsync(outputPath, CancellationToken.None);
            Assert.DoesNotContain("will be overwritten", written);
            Assert.NotNull(RpadParser.TryParse(written));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ConvertLinqAsync_Should_ReturnError_ForAnUnsupportedQueryKind()
    {
        var dir = Directory.CreateTempSubdirectory("runpad-cli-convert-test").FullName;
        try
        {
            var linqPath = Path.Combine(dir, "MyQuery.linq");
            await File.WriteAllTextAsync(linqPath, "<Query Kind=\"SQL\" />\n\nSELECT 1\n", CancellationToken.None);
            var stdout = new TestConsole();
            var stderr = new TestConsole();

            var exitCode = await CliRunner.RunAsync(["convert", linqPath], stdout, stderr, CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Contains("SQL", stderr.Output);
            Assert.False(File.Exists(Path.Combine(dir, "MyQuery.rpad")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
