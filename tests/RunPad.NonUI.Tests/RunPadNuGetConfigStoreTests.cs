using NuGet.Configuration;
using RunPad.Contracts;
using RunPad.Languages.DotnetCommon;
using Xunit;

namespace RunPad.NonUI.Tests;

// RunPad's own NuGet.Config: seeding from the user's settings, round-tripping the configurator's
// list (order, disabled sources, credentials) and keeping unrelated config. Temp folders only —
// never the real ApplicationData/RunPad or the user's NuGet settings.
public sealed class RunPadNuGetConfigStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("runpad-nuget-config").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private RunPadNuGetConfigStore Store(Func<ISettings?>? seed = null) => new(Dir("store"), seed ?? (() => null));

    [Fact]
    public void FirstUse_Should_SeedFromTheUsersSettings_IncludingDisabledSourcesCredentialsAndPackagesFolder()
    {
        var userDir = Dir("user");
        var packages = Path.Combine(_root, "packages");
        File.WriteAllText(Path.Combine(userDir, "NuGet.Config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="{packages}" /></config>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="corp feed" value="https://corp.example/nuget/v3/index.json" />
              </packageSources>
              <disabledPackageSources><add key="nuget.org" value="true" /></disabledPackageSources>
              <packageSourceCredentials>
                <corp_x0020_feed>
                  <add key="Username" value="me" />
                  <add key="ClearTextPassword" value="secret" />
                </corp_x0020_feed>
              </packageSourceCredentials>
            </configuration>
            """);
        var store = Store(() => Settings.LoadSpecificSettings(userDir, "NuGet.Config"));

        var sources = store.Load();

        Assert.Equal(
            [
                new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json", IsEnabled: false),
                new NuGetSource("corp feed", "https://corp.example/nuget/v3/index.json", true, "me", "secret"),
            ],
            sources);
        var seeded = Settings.LoadSpecificSettings(Path.GetDirectoryName(store.ConfigFilePath)!, "NuGet.Config");
        Assert.Equal(packages, SettingsUtility.GetGlobalPackagesFolder(seeded));
    }

    [Fact]
    public void FirstUse_WithoutUserSettings_Should_FallBackToNuGetOrg()
    {
        var store = Store();

        Assert.Equal([new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json")], store.Load());
        Assert.True(File.Exists(store.ConfigFilePath));
    }

    [Fact]
    public void Save_Should_RoundTripOrderDisabledSourcesAndCredentials_AndRaiseChanged()
    {
        var store = Store();
        var changed = 0;
        store.Changed += () => changed++;
        NuGetSource[] sources =
        [
            new("NuGet 3 official package source", "https://api.nuget.org/v3/index.json", true, "user name", "p@ss <word>"),
            new("local", Dir("feed"), IsEnabled: false),
            new("artifactory", "https://artifactory.example/api/nuget/v3/index.json"),
        ];

        store.Save(sources);

        Assert.Equal(1, changed);
        Assert.Equal(sources, store.Load());
        // NuGet encrypts on Windows (DPAPI); elsewhere it can only store clear text itself.
        var text = File.ReadAllText(store.ConfigFilePath);
        Assert.Equal(!OperatingSystem.IsWindows(), text.Contains("p@ss", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_Should_ReorderAndRemoveSources_AndKeepTheConfigSection()
    {
        var packages = Path.Combine(_root, "packages");
        var userDir = Dir("user");
        File.WriteAllText(Path.Combine(userDir, "NuGet.Config"), $"""
            <configuration>
              <config><add key="globalPackagesFolder" value="{packages}" /></config>
              <packageSources><add key="a" value="https://a.example/v3/index.json" /></packageSources>
            </configuration>
            """);
        var store = Store(() => Settings.LoadSpecificSettings(userDir, "NuGet.Config"));
        store.Save([new("a", "https://a.example/v3/index.json"), new("b", "https://b.example/v3/index.json"), new("c", "https://c.example/v3/index.json")]);

        store.Save([new("c", "https://c.example/v3/index.json"), new("a", "https://a.example/v3/index.json")]);

        Assert.Equal(["c", "a"], store.Load().Select(s => s.Name));
        var settings = Settings.LoadSpecificSettings(Path.GetDirectoryName(store.ConfigFilePath)!, "NuGet.Config");
        Assert.Equal(packages, SettingsUtility.GetGlobalPackagesFolder(settings));
    }

    [Fact]
    public void Save_Should_RecoverFromAHandBrokenFile()
    {
        var store = Store();
        File.WriteAllText(store.ConfigFilePath, "<configuration><packageSources>");

        store.Save([new("a", "https://a.example/v3/index.json")]);

        Assert.Equal(["a"], store.Load().Select(s => s.Name));
    }
}
