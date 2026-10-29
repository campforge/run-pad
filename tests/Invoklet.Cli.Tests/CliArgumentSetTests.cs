using Invoklet.Contracts;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Spectre.Console.Testing;
using Xunit;

namespace Invoklet.Cli.Tests;

// Which arguments a run from the command line gets: given ones, else the declared arguments with the
// values of --args-set or the selected set (or their defaults), then args:. The build is never reached: the language stops the run once it has the request.
public class CliArgumentSetTests
{
    private const string Script = """
        ---
        language: csharp
        arguments:
          - name: environment
            type: enum
            default: dev
            values: [dev, test]
          - name: file
            type: string
          - name: tags
            type: string[]
        argumentSets:
          smoke:
            environment: test
            file: "file with spaces.txt"
            tags: ["", "say \"hi\""]
          quiet: {}
          "ünï code":
            file: é
          broken:
            environment: prod
        {SELECTED}---

        ```csharp run
        1.Dump();
        ```

        """;

    private static readonly DotnetSdk Platform = new("net10.0", "10.0.100", ".NET 10.0.100", ".NET 10");

    private sealed class Stopped : Exception;

    private sealed class CapturingLanguage : ILanguageHostFactory
    {
        public ExecutionRequest? Request;
        public string Language => "csharp";
        public string DisplayName => "C#";
        public LanguageHostCapabilities Capabilities => default;
        public IReadOnlyDictionary<string, string> Snippets => new Dictionary<string, string>();
        public EditorLanguageProfile EditorProfile => new();
        public ILanguageHost Create(ILanguageHostOptions? options = null) => throw new Stopped();
        public ILanguageHost Create(ExecutionRequest request)
        {
            Request = request;
            throw new Stopped();
        }
    }

    private sealed class OneSdk : ISdkProvider
    {
        public IReadOnlyList<DotnetSdk> Get(string? dotnetExecutableOverride = null) => [Platform];
    }

    // The arguments the run got, or the exit code and error when it didn't start.
    private static async Task<(IReadOnlyList<string>? Arguments, int? Exit, string Error)> RunAsync(string? selected, params string[] commandLine)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Invoklet-cli-sets-{Guid.NewGuid():N}.rpad");
        await File.WriteAllTextAsync(path, Script.Replace("\r\n", "\n")
            .Replace("{SELECTED}", selected is null ? "" : $"selectedArgumentSet: {selected}\n"));
        try
        {
            var language = new CapturingLanguage();
            var languages = new LanguageHostRegistry();
            languages.Register(language);
            var stderr = new TestConsole().Width(1000);
            var services = new CliServices(languages, new OneSdk());
            var args = commandLine.Select(a => a == "{path}" ? path : a).ToArray();
            try
            {
                var exit = await CliRunner.RunAsync(args, services, new TestConsole(), stderr, CancellationToken.None);
                Assert.Null(language.Request);
                return (null, exit, stderr.Output);
            }
            catch (Stopped)
            {
                Assert.NotNull(language.Request!.ArgumentContractJson);
                Assert.Empty(language.Request.ArgumentContractErrors);
                var contract = RpadArgumentContract.FromJson(language.Request.ArgumentContractJson);
                Assert.Equal("environment", contract.Members.First().Name);
                return (language.Request!.Arguments, null, stderr.Output);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WithoutASelectedSet_Should_RunWithTheDefaults_ThenArgs()
        => Assert.Equal(["--environment", "dev"], (await RunAsync(null, "{path}")).Arguments);

    [Fact]
    public async Task TheSelectedSet_Should_GiveItsValues_WithEveryItemAsWritten()
        => Assert.Equal(["--environment", "test", "--file", "file with spaces.txt", "--tags", "", "--tags", "say \"hi\""],
            (await RunAsync("smoke", "{path}")).Arguments);

    [Theory]
    [InlineData("--args-set", "quiet")]
    [InlineData("--args-set=quiet")]
    public async Task ArgsSet_Should_ReplaceTheSelectedSet(params string[] option)
        => Assert.Equal(["--environment", "dev"], (await RunAsync("smoke", [.. option, "{path}"])).Arguments);

    [Fact]
    public async Task ArgsSet_Should_TakeANameWithUnicode()
        => Assert.Equal(["--environment", "dev", "--file", "é"], (await RunAsync(null, "--args-set", "ünï code", "{path}")).Arguments);

    // Arguments on the command line win over every set, and a bare -- means none at all.
    [Fact]
    public async Task GivenArguments_Should_WinOverArgsSetAndTheSelectedSet()
    {
        Assert.Equal(["a b", "-x"], (await RunAsync("smoke", "--args-set", "quiet", "{path}", "a b", "--", "-x")).Arguments);
        Assert.Empty((await RunAsync("smoke", "{path}", "--")).Arguments!);
    }

    [Fact]
    public async Task AnUnknownSet_Should_NotRun_AndNameTheScriptsSets()
    {
        var (_, exit, error) = await RunAsync(null, "--args-set", "nightly", "{path}");

        Assert.Equal(CliRunner.NotRunExitCode, exit);
        Assert.Contains("no argument set 'nightly'", error);
        Assert.Contains("smoke, quiet, ünï code, broken", error);
    }

    [Fact]
    public async Task ASelectedSetTheScriptDoesNotHave_Should_NotRun()
    {
        var (_, exit, error) = await RunAsync("gone", "{path}");

        Assert.Equal(CliRunner.NotRunExitCode, exit);
        Assert.Contains("no argument set 'gone'", error);
    }

    [Fact]
    public async Task AValueOfTheWrongType_Should_NotRun()
    {
        var (_, exit, error) = await RunAsync(null, "--args-set", "broken", "{path}");

        Assert.Equal(CliRunner.NotRunExitCode, exit);
        Assert.Contains("'environment' is 'prod', which is not one of dev, test", error);
    }

    [Fact]
    public void ArgsSet_Should_NeedAName()
    {
        Assert.Equal("The option 'args-set' needs a value.", CliArguments.Parse(["a.rpad", "--args-set"]).Error);
        Assert.Equal("smoke", CliArguments.Parse(["a.rpad", "--args-set", "smoke"]).ArgumentSet);
    }
}
