using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Invoklet.Build.Core.Models;
using Invoklet.Contracts.Models;
using Invoklet.Format.RPad;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Runtime.UI.Tests;
using Invoklet.Services.Secrets;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The store Invoklet writes and the script's Util.GetSecret reads: one file, two implementations of
// its format, so each test that matters goes through both. The tests that point Invoklet_SECRETS_FILE
// at a temporary store live in this one class, which xunit runs sequentially; it shares the real-build
// collection because one test runs a script, which inherits that variable.
[Collection(RealBuildTestCollection.Name)]
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-secrets-tests").FullName;
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
        store.Save(secrets, store.LoadSnapshot().Version);
        Assert.Equal(secrets, store.Load());

        store.Save([secrets[2]], store.LoadSnapshot().Version);
        Assert.Equal([secrets[2]], new FileSecretStore(StorePath).Load());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StorePath)!), f => f != StorePath + ".lock"); // no temporary files left
    }

    [Fact]
    public void TheFile_Should_BeEncryptedOnWindows_AndOwnerOnlyElsewhere()
    {
        SaveSecrets([new Secret("db", "plain-secret-value")]);
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
    [InlineData("{ not json", "The secret store file is damaged.")]
    [InlineData("{\"Version\":2,\"Protection\":\"None\",\"Secrets\":{}}", "The secret store was written by a newer Invoklet (format 2).")]
    [InlineData("{\"Protection\":\"None\",\"Secrets\":{}}", "The secret store file is damaged.")]
    [InlineData("{\"Version\":0,\"Protection\":\"None\",\"Secrets\":{}}", "The secret store file is damaged.")]
    [InlineData("{\"Version\":1,\"Protection\":\"Vault\",\"Secrets\":{}}", "The secret store uses an unknown protection (Vault).")]
    [InlineData("{\"Version\":1,\"Protection\":\"None\",\"Secrets\":{\"db\":null}}", "The secret store file is damaged.")]
    [InlineData("{\"Version\":1,\"Protection\":\"DPAPI\",\"Secrets\":{\"db\":null}}", "The secret store file is damaged.")]
    [InlineData("{\"Version\":1,\"Protection\":\"None\",\"Secrets\":{\"db\":\"a\",\"DB\":\"b\"}}",
        "The secret store file is damaged: two secrets are named 'DB' (letter case aside).")]
    public void AStoreThatCantBeRead_Should_FailToLoad_AndStayUnchanged(string content, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, content);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => new FileSecretStore(StorePath).Load()).Message);
        Assert.Equal(content, File.ReadAllText(StorePath));
    }

    [Fact]
    public void GetSecret_Should_ReadWhatInvokletSaved_LetterCaseAside()
    {
        SaveSecrets([new Secret("Orders-DB", "p@ss wörd"), new Secret("empty", "")]);
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

    // The script's own process, with the runtime library it ships with, reads the store Invoklet wrote.
    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task AScript_Should_ReadASecret_FromItsOwnProcess()
    {
        if (!new DotnetSdkProvider().Get().Any()) return;
        SaveSecrets([new Secret("api-token", "t0ken")]);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnOlderWindow_Should_NotOverwriteAnotherWindowsSecrets(bool existingFile, bool sharedStore)
    {
        var firstStore = new FileSecretStore(StorePath);
        if (existingFile) firstStore.Save([new Secret("db", "initial")], null);
        var first = new SecretStoreWindowViewModel(firstStore);
        var second = new SecretStoreWindowViewModel(sharedStore ? firstStore : new FileSecretStore(StorePath));
        ((System.Windows.Input.ICommand)second.AddCommand).Execute(null);
        second.SelectedSecret!.Name = "new-token";
        second.SelectedSecret.Value = "second-window";
        Assert.True(second.Save());
        var saved = File.ReadAllBytes(StorePath);
        if (!existingFile) ((System.Windows.Input.ICommand)first.AddCommand).Execute(null);
        first.Secrets[0].Value = "first-window";

        Assert.False(first.Save());
        Assert.Contains("changed in another window", first.SaveError);
        Assert.Equal(saved, File.ReadAllBytes(StorePath));
        Assert.Contains(firstStore.Load(), secret => secret.Name == "new-token");
        Assert.Equal("first-window", first.Secrets[0].Value);
    }

    private void SaveSecrets(IReadOnlyList<Secret> secrets)
    {
        var store = new FileSecretStore(StorePath);
        store.Save(secrets, store.LoadSnapshot().Version);
    }

    [Fact]
    public void AWriterLock_Should_BlockCompetingSave_WithoutChangingTheStore()
    {
        SaveSecrets([new Secret("db", "initial")]);
        var store = new FileSecretStore(StorePath);
        var snapshot = store.LoadSnapshot();
        var bytes = File.ReadAllBytes(StorePath);
        using var owner = new FileStream(StorePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<SecretStoreBusyException>(() => store.Save([new Secret("db", "changed")], snapshot.Version));
        Assert.Equal(bytes, File.ReadAllBytes(StorePath));
    }

    // Another window saving, or a script reading on Windows, holds the file only for a moment: the
    // window says to try again rather than to check access and disk space, and keeps the edits.
    [Fact]
    public void AStoreInUse_Should_AskToTryAgain_AndSaveOnceItIsFree()
    {
        SaveSecrets([new Secret("db", "initial")]);
        var vm = new SecretStoreWindowViewModel(new FileSecretStore(StorePath));
        vm.Secrets[0].Value = "changed";
        var owner = new FileStream(StorePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(vm.Save());
        Assert.Contains("in use", vm.SaveError);
        Assert.DoesNotContain("disk space", vm.SaveError);
        Assert.Equal("changed", vm.Secrets[0].Value);

        owner.Dispose();
        Assert.True(vm.Save());
        Assert.Equal("changed", Assert.Single(new FileSecretStore(StorePath).Load()).Value);
    }

    // A store kept elsewhere through a symbolic link stays a link: the file it points to is updated.
    [Fact]
    public void AStoreThatIsASymbolicLink_Should_StayOne_AndItsTargetBeUpdated()
    {
        var target = Path.Combine(_folder, "synced", "secrets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        new FileSecretStore(target).Save([new Secret("db", "initial")], null);
        try
        {
            File.CreateSymbolicLink(StorePath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Symbolic links can't be created here: {ex.Message}");
        }

        SaveSecrets([new Secret("db", "changed")]);

        Assert.NotNull(new FileInfo(StorePath).LinkTarget);
        Assert.Equal("changed", Assert.Single(new FileSecretStore(target).Load()).Value);
        Assert.Equal("changed", Assert.Single(new FileSecretStore(StorePath).Load()).Value);
    }

    // A save killed between writing its temporary file and moving it leaves that file (on macOS and
    // Linux with the secrets in it); the next save removes it, and nothing else in the folder.
    [Fact]
    public void ASave_Should_RemoveTemporaryFilesLeftByAKilledSave_AndNothingElse()
    {
        SaveSecrets([new Secret("db", "initial")]);
        var folder = Path.GetDirectoryName(StorePath)!;
        var leftover = Path.Combine(folder, "secrets.json." + Guid.NewGuid().ToString("N") + ".tmp");
        var unrelated = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(leftover, "{\"Secrets\":{\"db\":\"stale\"}}");
        File.WriteAllText(unrelated, "someone else's");

        SaveSecrets([new Secret("db", "changed")]);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(unrelated));
        Assert.Empty(Directory.GetFiles(folder, "secrets.json.*.tmp"));
    }

    [Fact]
    public void ASuccessfulSave_Should_AdvanceTheWindowsVersion_ForItsNextSave()
    {
        var store = new FileSecretStore(StorePath);
        var vm = new SecretStoreWindowViewModel(store);
        ((System.Windows.Input.ICommand)vm.AddCommand).Execute(null);
        vm.SelectedSecret!.Value = "first";
        Assert.True(vm.Save());
        vm.SelectedSecret.Value = "second";
        Assert.True(vm.Save());
        Assert.Equal("second", Assert.Single(store.Load()).Value);
    }

    [Fact]
    public void ARealWriteFailure_Should_KeepEditsAndSaveAfterThePathIsFixed()
    {
        var store = new FileSecretStore(StorePath);
        var vm = new SecretStoreWindowViewModel(store);
        ((System.Windows.Input.ICommand)vm.AddCommand).Execute(null);
        vm.SelectedSecret!.Value = "retained-value";
        Directory.CreateDirectory(StorePath);
        Assert.False(vm.Save());
        Assert.NotNull(vm.SaveError);
        Assert.DoesNotContain("retained-value", vm.SaveError);
        Assert.Equal("retained-value", vm.SelectedSecret.Value);
        Directory.Delete(StorePath);
        Assert.True(vm.Save());
        Assert.Null(vm.SaveError);
        Assert.Equal("retained-value", Assert.Single(store.Load()).Value);
    }

    [Fact]
    public void ARemovedStore_Should_NotBeRecreatedFromAStaleWindow()
    {
        SaveSecrets([new Secret("db", "initial")]);
        var vm = new SecretStoreWindowViewModel(new FileSecretStore(StorePath));
        File.Delete(StorePath);
        Assert.False(vm.Save());
        Assert.False(File.Exists(StorePath));
        Assert.Contains("changed in another window", vm.SaveError);
        Assert.Equal("initial", vm.Secrets[0].Value);
    }

    private static void WithStore(string path, Action test)
    {
        var previous = Environment.GetEnvironmentVariable(FileSecretStore.FileEnvVar);
        Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, path);
        try { test(); }
        finally { Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, previous); }
    }
}
