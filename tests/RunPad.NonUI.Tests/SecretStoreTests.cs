using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Runtime.UI.Tests;
using RunPad.Services.Secrets;
using Xunit;

namespace RunPad.NonUI.Tests;

// The store RunPad writes and the script's Util.GetSecret reads: one file, two implementations of
// its format, so each test that matters goes through both. The tests that point RUNPAD_SECRETS_FILE
// at a temporary store live in this one class, which xunit runs sequentially; it shares the real-build
// collection because one test runs a script, which inherits that variable.
[Collection(RealBuildTestCollection.Name)]
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-secrets-tests").FullName;
    private string StorePath => Path.Combine(_folder, "nested", "secrets.json");

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void NoStoreYet_Should_LoadAsEmpty()
        => Assert.Empty(new FileSecretStore(StorePath).Load());

    [Fact]
    public void SavedSecrets_Should_LoadBackInOrder()
    {
        var store = new FileSecretStore(StorePath);
        Secret[] secrets = [new("db", "p@ss \"wörd\"\n"), new("api-token", ""), new("Zeta", "z")];
        store.Save(secrets);
        Assert.Equal(secrets, store.Load());

        store.Save([secrets[2]]);
        Assert.Equal([secrets[2]], new FileSecretStore(StorePath).Load());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StorePath)!)); // no temporary files left
    }

    [Fact]
    public void TheFile_Should_BeEncryptedOnWindows_AndOwnerOnlyElsewhere()
    {
        new FileSecretStore(StorePath).Save([new Secret("db", "plain-secret-value")]);
        var json = JsonNode.Parse(File.ReadAllText(StorePath))!;
        Assert.Equal(1, json["Version"]!.GetValue<int>());
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("DPAPI", json["Protection"]!.GetValue<string>());
            Assert.DoesNotContain("plain-secret-value", File.ReadAllText(StorePath));
        }
        else
        {
            Assert.Equal("None", json["Protection"]!.GetValue<string>());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Version\":2,\"Protection\":\"None\",\"Secrets\":{}}")]
    [InlineData("{\"Version\":1,\"Protection\":\"Vault\",\"Secrets\":{}}")]
    public void AStoreThatCantBeRead_Should_FailToLoad_AndStayUnchanged(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, content);
        Assert.Throws<InvalidDataException>(() => new FileSecretStore(StorePath).Load());
        Assert.Equal(content, File.ReadAllText(StorePath));
    }

    [Fact]
    public void GetSecret_Should_ReadWhatRunPadSaved_LetterCaseAside()
    {
        new FileSecretStore(StorePath).Save([new Secret("Orders-DB", "p@ss wörd"), new Secret("empty", "")]);
        WithStore(StorePath, () =>
        {
            Assert.Equal("p@ss wörd", Util.GetSecret("orders-db"));
            Assert.Equal("", Util.GetSecret("EMPTY"));
            var missing = Assert.Throws<KeyNotFoundException>(() => Util.GetSecret("billing"));
            Assert.Contains("'billing'", missing.Message);
            Assert.Contains("Tools → Secret Store", missing.Message);
        });
    }

    [Fact]
    public void GetSecret_WithoutAStore_Should_SayWhereToAddTheSecret()
    {
        WithStore(StorePath, () =>
        {
            var missing = Assert.Throws<KeyNotFoundException>(() => Util.GetSecret("db"));
            Assert.Contains(StorePath, missing.Message);
        });
    }

    [Fact]
    public void GetSecret_FromADamagedStore_Should_SaySo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{ not json");
        WithStore(StorePath, () => Assert.Throws<InvalidDataException>(() => Util.GetSecret("db")));
    }

    [Fact]
    public void TheDefaultPath_Should_FollowTheEnvironmentVariable()
        => WithStore(StorePath, () => Assert.Equal(StorePath, new FileSecretStore().FilePath));

    // The script's own process, with the runtime library it ships with, reads the store RunPad wrote.
    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task AScript_Should_ReadASecret_FromItsOwnProcess()
    {
        if (!new DotnetSdkProvider().Get().Any()) return;
        new FileSecretStore(StorePath).Save([new Secret("api-token", "t0ken")]);
        var previous = Environment.GetEnvironmentVariable(FileSecretStore.FileEnvVar);
        Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, StorePath);
        try
        {
            var outputDir = Path.Combine(_folder, "build");
            var options = new CompiledDotnetLanguageHostOptions
            {
                Platform = new DotnetSdkProvider().Get().First(),
                ExecutionOptions = new ExecutionOptions
                {
                    WorkingDirectory = outputDir,
                    Runtime = new RuntimeOptions { RuntimePath = "dotnet" },
                    Debugger = new DebuggerOptions { DebuggerPath = "" },
                },
                OutputDirectory = outputDir,
                RuntimeLibPath = new CSharpReferenceService().GetExtensionReference(),
                Kind = RpadKind.Statements,
            };
            Directory.CreateDirectory(outputDir);
            await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
            var output = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            const string script = """
                Console.WriteLine(Util.GetSecret("API-TOKEN"));
                try { Util.GetSecret("missing"); }
                catch (KeyNotFoundException ex) { Console.WriteLine(ex.Message); }
                """;
            var result = await host.RunAsync(script, (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data),
                    TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, string.Join('\n', errors));
            Assert.Contains("t0ken", output);
            Assert.Contains(output, line => line.Contains("'missing'", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, previous); }
    }

    private static void WithStore(string path, Action test)
    {
        var previous = Environment.GetEnvironmentVariable(FileSecretStore.FileEnvVar);
        Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, path);
        try { test(); }
        finally { Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, previous); }
    }
}
