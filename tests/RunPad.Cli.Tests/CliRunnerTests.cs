using RunPad.Cli;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp.Sdk;
using RunPad.Languages.DotnetCommon;
using Spectre.Console.Testing;
using Xunit;

namespace RunPad.Cli.Tests;

// BuildOptions is the one piece of CliRunner.RunAsync that's pure logic (no process
// spawning, no real SDK needed) — mirrors EditorTabItemControl.BuildRunOptions for the GUI's own
// non-debug run path, so these pin down that the two stay equivalent.
public class CliRunnerTests
{
    private static readonly DotnetSdk Platform = new("net10.0", "10.0.100", ".NET 10.0.100", ".NET 10");

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
        var options = CliRunner.BuildOptions(CreateFrontMatter(), Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(Platform, csharpOptions.Platform);
        Assert.Equal("/tmp/out", csharpOptions.OutputDirectory);
        Assert.Equal(RpadKind.Program, csharpOptions.Kind);
        Assert.Equal("13.0.3", csharpOptions.Packages["Newtonsoft.Json"]);
        Assert.Equal(["System.Text"], csharpOptions.Usings);
        Assert.Equal(["Some.Native.dll"], csharpOptions.AssemblyReferences);
        Assert.Equal(["Some.Project.csproj", "Some.Vb.vbproj"], csharpOptions.ProjectReferences);
        Assert.False(string.IsNullOrEmpty(csharpOptions.RuntimeLibPath));
    }

    [Fact]
    public void BuildOptions_Should_ReturnExpectedOptions_ForVb()
    {
        var options = CliRunner.BuildOptions(CreateFrontMatter(RpadLanguage.Vb), Platform, "/tmp/out");

        var vbOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(Platform, vbOptions.Platform);
        Assert.Equal(RpadKind.Program, vbOptions.Kind);
    }

    [Fact]
    public void BuildOptions_Should_DefaultToEmptyCollections_WhenFrontMatterHasNone()
    {
        var frontMatter = new RpadFrontMatter(); // Namespaces/References both null by default

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out");

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
        // Path.GetDirectoryName normalizes to the platform's own separator (backslashes on
        // Windows) — computing the expected directory the same way, rather than hardcoding a
        // forward-slash-only "/home/user/..." literal, keeps this correct on every CI OS.
        var currentQueryPath = Path.Combine("home", "user", "scripts", "query.rpad");
        var expectedDir = Path.GetDirectoryName(currentQueryPath);
        var frontMatter = new RpadFrontMatter
        {
            References = ["$(CurrentQueryPath)/Lib.dll", "$(CurrentQueryPath)/Shared.csproj"],
        };

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out", currentQueryPath);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal([expectedDir + "/Lib.dll"], csharpOptions.AssemblyReferences);
        Assert.Equal([expectedDir + "/Shared.csproj"], csharpOptions.ProjectReferences);
    }

    [Fact]
    public void BuildOptions_Should_LeaveCurrentQueryPathMacroUnexpanded_WhenNoFilePathGiven()
    {
        var frontMatter = new RpadFrontMatter { References = ["$(CurrentQueryPath)/Lib.dll"] };

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(["$(CurrentQueryPath)/Lib.dll"], csharpOptions.AssemblyReferences);
    }

    // Distinct from the $(CurrentQueryPath)
    // reference-macro tests above: this is the options OBJECT's own CurrentQueryPath property (what
    // actually reaches the compiled script's process as an environment variable), not just a string
    // substitution in References.
    [Fact]
    public void BuildOptions_Should_SetCurrentQueryPath_OnTheOptionsObject()
    {
        var currentQueryPath = Path.Combine("home", "user", "scripts", "query.rpad");

        var options = CliRunner.BuildOptions(CreateFrontMatter(), Platform, "/tmp/out", currentQueryPath);

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Equal(currentQueryPath, csharpOptions.CurrentQueryPath);
    }

    [Fact]
    public void BuildOptions_Should_LeaveCurrentQueryPathNull_WhenNoFilePathGiven()
    {
        var options = CliRunner.BuildOptions(CreateFrontMatter(), Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        Assert.Null(csharpOptions.CurrentQueryPath);
    }

    [Fact]
    public void BuildOptions_Should_ExpandRuntimeDirectoryAndMyDocumentsMacros_InReferences()
    {
        var frontMatter = new RpadFrontMatter { References = ["$(RuntimeDirectory)/System.dll"] };

        var options = CliRunner.BuildOptions(frontMatter, Platform, "/tmp/out");

        var csharpOptions = Assert.IsType<CompiledDotnetLanguageHostOptions>(options);
        var expanded = Assert.Single(csharpOptions.AssemblyReferences);
        Assert.DoesNotContain("$(RuntimeDirectory)", expanded);
        Assert.EndsWith("/System.dll", expanded);
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
            CliRunner.BuildOptions(frontMatter, Platform, Path.GetTempPath()));
        var fromCommandLine = Assert.IsType<CompiledDotnetLanguageHostOptions>(
            CliRunner.BuildOptions(frontMatter, Platform, Path.GetTempPath(), arguments: ["cli"]));

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
