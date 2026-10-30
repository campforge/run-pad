using Invoklet.NonUI.Tests.App.Services.Secrets.Fixtures;
using Invoklet.Services.Secrets;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Secrets;

// The names of the secrets are not encrypted, so listing them never needs the key of the system keychain, which may ask the user to unlock it.
public sealed class FileSecretStoreNamesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("Invoklet-secret-names").FullName;
    private string StorePath => Path.Combine(_folder, "secrets.json");

    public void Dispose() => Directory.Delete(_folder, true);

    private (FileSecretStore Store, CountingKeySource Keys) EncryptedStore()
    {
        var keys = new CountingKeySource();
        var writer = new FileSecretStore(StorePath, keys);
        writer.Save([new Secret("db", "p1"), new Secret("api", "p2")], writer.LoadSnapshot().Version);
        // A later session: the same keychain, asked for nothing yet.
        var later = new CountingKeySource();
        later.GetOrCreate();
        return (new FileSecretStore(StorePath, later), later);
    }

    [Fact]
    public void LoadNames_AnEncryptedStore_ListsTheNamesAndNeverAsksTheKeychain()
    {
        // Arrange
        var (store, keys) = EncryptedStore();
        var asked = keys.FindCalls;

        // Act
        var names = store.LoadNames();

        // Assert
        Assert.Equal(["db", "api"], names);
        Assert.Equal(asked, keys.FindCalls);
    }

    [Fact]
    public void LoadSnapshot_AnEncryptedStore_AsksTheKeychainForItsKey()
    {
        // Arrange
        var (store, keys) = EncryptedStore();
        var asked = keys.FindCalls;

        // Act
        var snapshot = store.LoadSnapshot();

        // Assert
        Assert.Equal(["p1", "p2"], snapshot.Secrets.Select(s => s.Value));
        Assert.Equal(asked + 1, keys.FindCalls);
    }

    [Fact]
    public void LoadNames_NoStoreYet_IsEmpty()
        => Assert.Empty(new FileSecretStore(StorePath, new CountingKeySource()).LoadNames());

    [Fact]
    public void LoadNames_ADamagedStore_ReportsItAsLoadSnapshotDoes()
    {
        // Arrange
        File.WriteAllText(StorePath, "{ not json");
        var store = new FileSecretStore(StorePath, new CountingKeySource());

        // Act
        var thrown = Record.Exception(store.LoadNames);

        // Assert
        Assert.IsType<InvalidDataException>(thrown);
    }

    [Fact]
    public void LoadNames_TwoNamesDifferingOnlyInCase_IsReportedAsDamaged()
    {
        // Arrange
        File.WriteAllText(StorePath, "{\"Version\":1,\"Protection\":\"None\",\"Secrets\":{\"Db\":\"a\",\"db\":\"b\"}}");
        var store = new FileSecretStore(StorePath, new CountingKeySource());

        // Act
        var thrown = Record.Exception(store.LoadNames);

        // Assert
        Assert.IsType<InvalidDataException>(thrown);
    }
}
