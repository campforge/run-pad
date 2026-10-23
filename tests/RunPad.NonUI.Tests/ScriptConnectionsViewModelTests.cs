using System.Reactive.Linq;
using System.Windows.Input;
using Moq;
using RunPad.Connectors.Http;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// Dependencies → Connections: adding the user's connections to a script, removing them, importing a
// script's connection into the user's list, and what each one's status says.
public class ScriptConnectionsViewModelTests
{
    private static RpadConnection Http(string name, string address, string? token = null) => new()
    {
        Name = name,
        Driver = "http",
        Params = token is null
            ? new(StringComparer.Ordinal) { ["baseAddress"] = RpadValue.Of(address) }
            : new(StringComparer.Ordinal)
            {
                ["baseAddress"] = RpadValue.Of(address),
                ["headers"] = RpadValue.Map([new("Authorization", RpadValue.Of($"Bearer ${{secret:{token}}}"))]),
            },
    };

    private sealed class SqlLikeDriver : IConnectionDriver
    {
        public string Id => "sqlish";
        public string DisplayName => "SQL-ish";
        public ConnectionCategory Category => ConnectionCategory.Database;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Microsoft.Data.Sqlite"] = "9.0.0" };
        public IReadOnlyList<string> Validate(RpadConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) => new("");
    }

    private static (DependenciesWindowViewModel Vm, Mock<IConnectionStore> Store, List<RpadConnection> Mine) CreateVm(params RpadConnection[] mine)
    {
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([new Secret("billing-token", "t0k")], null));
        return CreateVm(secrets, mine);
    }

    private static (DependenciesWindowViewModel Vm, Mock<IConnectionStore> Store, List<RpadConnection> Mine) CreateVm(
        Mock<ISecretStore> secrets, RpadConnection[] mine)
    {
        var list = mine.ToList();
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(() => new ConnectionStoreSnapshot([.. list], null));
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()))
            .Callback<IReadOnlyList<RpadConnection>, string?>((saved, _) => { list.Clear(); list.AddRange(saved); });
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new HttpConnectionDriver());
        drivers.Register(new SqlLikeDriver());
        var sources = new Mock<INuGetSourceStore>();
        sources.Setup(s => s.Load()).Returns([]);
        var cache = new Mock<ILocalPackageCacheService>();
        cache.Setup(c => c.GetCachedPackages()).Returns([]);
        var vm = new DependenciesWindowViewModel(Mock.Of<IPackageSearchService>(), cache.Object, sources.Object, TimeSpan.Zero,
            store.Object, secrets.Object, drivers);
        return (vm, store, list);
    }

    private static void Execute(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
    }

    [Fact]
    public void AddingYourConnection_Should_PutItInTheScript_OnSave()
    {
        var billing = Http("billing", "https://billing.local/", "billing-token");
        var (vm, _, _) = CreateVm(billing, Http("github", "https://api.github.com/"));
        var fm = new RpadFrontMatter { Connections = [Http("billing", "https://billing.local/", "billing-token")] };
        vm.SetFrontMatter(fm);
        var connections = vm.Connections;

        var item = Assert.Single(connections.Items);
        Assert.Equal("Connections.Billing", item.CodeName);
        Assert.Equal("HTTP", item.DriverName);
        Assert.Equal("Same as in your connections.", item.Status);
        Assert.Null(item.Problem);
        Assert.Equal(["github"], connections.Available.Select(c => c.Name)); // billing is already in the script
        Assert.False(vm.HasChanges);

        Execute(connections.AddCommand);
        Assert.Equal(["billing", "github"], connections.Items.Select(i => i.Name));
        Assert.Empty(connections.Available);
        Assert.True(vm.HasChanges);

        Assert.True(vm.ApplyChanges());
        Assert.Equal(["billing", "github"], fm.Connections.Select(c => c.Name));
        Assert.Equal("https://api.github.com/", fm.Connections[1].Params["baseAddress"].Text);
    }

    [Fact]
    public void Removing_Should_TakeTheConnectionOutOfTheScript()
    {
        var (vm, _, _) = CreateVm();
        var fm = new RpadFrontMatter { Connections = [Http("a", "https://a/"), Http("b", "https://b/")] };
        vm.SetFrontMatter(fm);
        vm.Connections.SelectedItem = vm.Connections.Items[0];
        Execute(vm.Connections.RemoveCommand);
        Assert.Equal("b", vm.Connections.SelectedItem?.Name);
        Assert.True(vm.ApplyChanges());
        Assert.Equal(["b"], fm.Connections.Select(c => c.Name));
    }

    [Fact]
    public void Import_Should_KeepTheScriptsConnectionInYours_WithoutChangingTheScript()
    {
        var (vm, store, mine) = CreateVm(Http("billing", "https://old.billing/"));
        vm.SetFrontMatter(new RpadFrontMatter { Connections = [Http("billing", "https://billing.local/"), Http("orders", "https://orders/")] });
        Assert.Equal("Differs from your connection of the same name: Import replaces yours.", vm.Connections.Items[0].Status);
        Assert.Equal("Not in your connections: Import keeps it there.", vm.Connections.Items[1].Status);

        vm.Connections.SelectedItem = vm.Connections.Items[0];
        Execute(vm.Connections.ImportCommand);
        Assert.True(vm.Connections.IsImportConfirmationPending);
        Assert.Equal("https://old.billing/", mine[0].Params["baseAddress"].Text);
        Execute(vm.Connections.ConfirmImportCommand);
        vm.Connections.SelectedItem = vm.Connections.Items[1];
        Execute(vm.Connections.ImportCommand);

        Assert.Equal(["billing", "orders"], mine.Select(c => c.Name));
        Assert.Equal("https://billing.local/", mine[0].Params["baseAddress"].Text);
        Assert.All(vm.Connections.Items, i => Assert.Equal("Same as in your connections.", i.Status));
        Assert.False(vm.HasChanges);
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public void Problems_Should_SayWhatWouldFailTheBuild()
    {
        var (vm, _, _) = CreateVm();
        vm.SetFrontMatter(new RpadFrontMatter
        {
            Connections =
            [
                Http("billing", "https://billing.local/", "missing-token"),
                new RpadConnection { Name = "warehouse", Driver = "snowflake" },
                new RpadConnection { Name = "broken", Driver = "http" },
            ],
        });
        Assert.Equal(
            ["The Secret Store has no secret named 'missing-token'.", "This RunPad has no 'snowflake' driver.", "The base address is missing."],
            vm.Connections.Items.Select(i => i.Problem));

        vm.SetFrontMatter(new RpadFrontMatter { Language = RpadLanguage.Vb, Connections = [Http("a", "https://a/")] });
        Assert.Equal("Connections can be used in C# scripts only, for now.", Assert.Single(vm.Connections.Items).Problem);
    }

    // The libraries a connection needs come as the script's own packages, visible on the Packages tab.
    [Fact]
    public void AddingAConnection_Should_AddItsDriversPackages_AndAMissingOneIsAProblem()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "local", Driver = "sqlish" });
        var fm = new RpadFrontMatter();
        vm.SetFrontMatter(fm);
        Execute(vm.Connections.AddCommand);
        Assert.True(vm.Connections.IsAddConfirmationPending);
        Assert.Equal("Add 'local' (SQL-ish) to this script? Its client is built into the script, which also gets the NuGet package the connection needs: Microsoft.Data.Sqlite 9.0.0. Cancel leaves the script as it is.", vm.Connections.AddMessage);
        Assert.Empty(vm.Connections.Items); // nothing changes before confirming
        Execute(vm.Connections.ConfirmAddCommand);
        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Null(vm.Connections.Items.Single().Problem);
        Assert.True(vm.ApplyChanges());
        Assert.Equal("9.0.0", fm.Packages["Microsoft.Data.Sqlite"].Version);

        var (other, _, _) = CreateVm();
        other.SetFrontMatter(new RpadFrontMatter { Connections = [new RpadConnection { Name = "local", Driver = "sqlish" }] });
        Assert.Equal("Needs the NuGet package Microsoft.Data.Sqlite: add it on the Packages tab.", other.Connections.Items.Single().Problem);
    }

    // Cancelling the package confirmation leaves the script, its packages and the available list
    // exactly as they were; the connection can still be chosen again afterward.
    [Fact]
    public void CancellingAnAdd_Should_ChangeNothing()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "local", Driver = "sqlish" });
        vm.SetFrontMatter(new RpadFrontMatter());
        var connections = vm.Connections;
        connections.SelectedAvailable = connections.Available.Single();
        Execute(connections.AddCommand);
        Assert.True(connections.IsAddConfirmationPending);

        Execute(connections.CancelAddCommand);
        Assert.False(connections.IsAddConfirmationPending);
        Assert.Null(connections.AddMessage);
        Assert.Empty(connections.Items);
        Assert.Equal(["local"], connections.Available.Select(c => c.Name));
        Assert.False(vm.HasChanges);

        // The dropdown's own selection change also drops a pending confirmation for the old choice.
        connections.SelectedAvailable = connections.Available.Single();
        Execute(connections.AddCommand);
        connections.SelectedAvailable = null;
        Assert.False(connections.IsAddConfirmationPending);
    }

    // A package the driver needs that the script already has, under any version, is not asked about:
    // adding never silently overwrites a version the user chose, so there is nothing to confirm.
    [Fact]
    public void Add_Should_NotAsk_WhenTheNeededPackageIsAlreadyInTheDraft()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "local", Driver = "sqlish" });
        var fm = new RpadFrontMatter { Packages = new() { ["Microsoft.Data.Sqlite"] = new Package("8.0.0") } };
        vm.SetFrontMatter(fm);
        Execute(vm.Connections.AddCommand);
        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Single(vm.Connections.Items);
        Assert.True(vm.ApplyChanges());
        Assert.Equal("8.0.0", fm.Packages["Microsoft.Data.Sqlite"].Version); // kept, not overwritten
    }

    // ── New Connection / Manage Connections ──────────────────────────────────

    private static void UseEditor(DependenciesWindowViewModel vm, List<RpadConnection> mine, Func<string?, IReadOnlyList<RpadConnection>?> edit,
        List<string?>? opened = null)
    {
        vm.Connections.ConnectionsEditor = driverId =>
        {
            opened?.Add(driverId);
            // The window saved a new list, or closed without saving.
            if (edit(driverId) is not { } saved) return Task.FromResult(false);
            mine.Clear();
            mine.AddRange(saved);
            return Task.FromResult(true);
        };
    }

    [Fact]
    public async Task ANewConnection_Should_BeOfferedToTheScript_ThroughTheSameConfirmation()
    {
        var (vm, _, mine) = CreateVm(Http("github", "https://api.github.com/"));
        var fm = new RpadFrontMatter();
        vm.SetFrontMatter(fm);
        var opened = new List<string?>();
        UseEditor(vm, mine, _ => [.. mine, new RpadConnection { Name = "local", Driver = "sqlish" }], opened);
        var connections = vm.Connections;
        var sqlish = Assert.Single(connections.Drivers, d => d.Id == "sqlish");

        await connections.NewCommand.Execute(sqlish);

        Assert.Equal(["sqlish"], opened);
        Assert.Equal(["github", "local"], connections.Available.Select(c => c.Name));
        Assert.True(connections.IsAddConfirmationPending);
        Assert.Contains("'local'", connections.AddMessage);
        Assert.Contains("Microsoft.Data.Sqlite 9.0.0", connections.AddMessage);
        Assert.Empty(connections.Items); // the script is untouched until the user confirms
        Assert.False(vm.HasChanges);

        Execute(connections.CancelAddCommand);
        Assert.Empty(connections.Items);
        Assert.False(vm.HasChanges);
        Assert.Contains(connections.Available, c => c.Name == "local"); // still saved, still addable

        connections.SelectedAvailable = connections.Available.Single(c => c.Name == "local");
        Execute(connections.AddCommand);
        Execute(connections.ConfirmAddCommand);
        Assert.True(vm.ApplyChanges());
        Assert.Equal(["local"], fm.Connections.Select(c => c.Name));
        Assert.Equal("9.0.0", fm.Packages["Microsoft.Data.Sqlite"].Version);
    }

    [Fact]
    public async Task ANewConnectionWithNoNewPackages_Should_BeAddedWithoutAskingAgain()
    {
        var (vm, _, mine) = CreateVm();
        var fm = new RpadFrontMatter { Packages = new() { ["Microsoft.Data.Sqlite"] = new Package("8.0.0") } };
        vm.SetFrontMatter(fm);
        UseEditor(vm, mine, _ => [new RpadConnection { Name = "second", Driver = "sqlish" }]);

        await vm.Connections.NewCommand.Execute(Assert.Single(vm.Connections.Drivers, d => d.Id == "sqlish"));

        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Equal(["second"], vm.Connections.Items.Select(i => i.Name));
        Assert.True(vm.ApplyChanges());
        Assert.Equal("8.0.0", fm.Packages["Microsoft.Data.Sqlite"].Version); // the version the script chose is kept
    }

    [Fact]
    public async Task ClosingTheConnectionsWindowWithoutSaving_Should_ChangeNothing()
    {
        var (vm, store, mine) = CreateVm(Http("github", "https://api.github.com/"));
        vm.SetFrontMatter(new RpadFrontMatter());
        UseEditor(vm, mine, _ => null);
        var loads = store.Invocations.Count;

        await vm.Connections.NewCommand.Execute(Assert.Single(vm.Connections.Drivers, d => d.Id == "sqlish"));

        Assert.Equal(loads, store.Invocations.Count); // the list wasn't read again
        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Empty(vm.Connections.Items);
        Assert.Equal(["github"], vm.Connections.Available.Select(c => c.Name));
        Assert.False(vm.HasChanges);
        Assert.Null(vm.Connections.CreateMessage);
    }

    [Fact]
    public async Task OpeningTheEditor_Should_DropAPendingConfirmation_AndKeepTheDraft()
    {
        var (vm, _, mine) = CreateVm(Http("github", "https://api.github.com/"), new RpadConnection { Name = "local", Driver = "sqlish" });
        var fm = new RpadFrontMatter();
        vm.SetFrontMatter(fm);
        Execute(vm.Connections.AddCommand); // github: no confirmation, a pending edit of the draft
        vm.Connections.SelectedAvailable = vm.Connections.Available.Single(c => c.Name == "local");
        Execute(vm.Connections.AddCommand);
        Assert.True(vm.Connections.IsAddConfirmationPending);
        UseEditor(vm, mine, _ => null);

        await vm.Connections.ManageCommand.Execute();

        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Equal(["github"], vm.Connections.Items.Select(i => i.Name));
        Assert.True(vm.HasChanges); // the connection added before is still a pending edit
        Assert.True(vm.ApplyChanges());
        Assert.Equal(["github"], fm.Connections.Select(c => c.Name));
        Assert.True(fm.Packages is null or { Count: 0 }); // the pending SQL connection brought none
    }

    [Fact]
    public async Task ManagingConnections_Should_ReloadTheList_WithoutAddingAnythingToTheScript()
    {
        var (vm, _, mine) = CreateVm(Http("github", "https://api.github.com/"));
        vm.SetFrontMatter(new RpadFrontMatter());
        UseEditor(vm, mine, _ => [.. mine, new RpadConnection { Name = "local", Driver = "sqlish" }]);

        await vm.Connections.ManageCommand.Execute();

        Assert.Equal(["github", "local"], vm.Connections.Available.Select(c => c.Name));
        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Empty(vm.Connections.Items);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task ANewConnectionWhoseCodeNameTheScriptHas_Should_NotBeAdded_AndSayWhy()
    {
        var (vm, _, mine) = CreateVm(Http("my-api", "https://one/"));
        vm.SetFrontMatter(new RpadFrontMatter { Connections = [Http("my-api", "https://one/")] });
        UseEditor(vm, mine, _ => [.. mine, Http("my_api", "https://two/")]);

        await vm.Connections.NewCommand.Execute(Assert.Single(vm.Connections.Drivers, d => d.Id == "http"));

        Assert.Single(vm.Connections.Items);
        Assert.Contains("'my_api'", vm.Connections.CreateMessage);
        Assert.Contains("already has", vm.Connections.CreateMessage);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task AnEditorThatFails_Should_BeReported_WithoutChangingTheScript()
    {
        var (vm, _, _) = CreateVm(Http("github", "https://api.github.com/"));
        vm.SetFrontMatter(new RpadFrontMatter());
        vm.Connections.ConnectionsEditor = _ => throw new InvalidOperationException("no window");

        await vm.Connections.ManageCommand.Execute();

        Assert.Contains("no window", vm.Connections.CreateMessage);
        Assert.Empty(vm.Connections.Items);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void TheEditorCommands_Should_BeOff_WithoutAnEditor_OrWhenTheListCantBeRead()
    {
        var (vm, store, _) = CreateVm();
        vm.SetFrontMatter(new RpadFrontMatter());
        Assert.False(vm.Connections.CanEditConnections);
        Assert.False(((ICommand)vm.Connections.ManageCommand).CanExecute(null));

        vm.Connections.ConnectionsEditor = _ => Task.FromResult(false);
        Assert.True(vm.Connections.CanEditConnections);

        store.Setup(s => s.LoadSnapshot()).Throws(new InvalidDataException("broken"));
        vm.SetFrontMatter(new RpadFrontMatter());
        Assert.True(vm.Connections.HasReadError);
        Assert.False(vm.Connections.CanEditConnections);
    }

    [Fact]
    public void AConnectionWithADriverThisRunPadLacks_Should_NotBeAdded_AndExplainWhatIsMissing()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "legacy", Driver = "oracle" });
        vm.SetFrontMatter(new RpadFrontMatter());

        Execute(vm.Connections.AddCommand);

        Assert.Empty(vm.Connections.Items);
        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Contains("'oracle' driver", vm.Connections.CreateMessage);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void AddingAnotherConnectionOfADriverWhosePackagesCameEarlier_Should_NotAskAgain()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "one", Driver = "sqlish" }, new RpadConnection { Name = "two", Driver = "sqlish" });
        vm.SetFrontMatter(new RpadFrontMatter());
        Execute(vm.Connections.AddCommand);
        Assert.True(vm.Connections.IsAddConfirmationPending);
        Execute(vm.Connections.ConfirmAddCommand);

        Execute(vm.Connections.AddCommand);

        Assert.False(vm.Connections.IsAddConfirmationPending);
        Assert.Equal(["one", "two"], vm.Connections.Items.Select(i => i.Name));
    }

    // Reading the Secret Store decrypts every value; the tab needs only the names, once per opening,
    // not on each package edit (each of which refreshes the connections' problems).
    [Fact]
    public void PackageEdits_Should_NotReadTheSecretStoreAgain()
    {
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([new Secret("billing-token", "t0k")], null));
        var (vm, _, _) = CreateVm(secrets, [new RpadConnection { Name = "local", Driver = "sqlish" }, Http("other", "https://other/")]);
        vm.SetFrontMatter(new RpadFrontMatter { Connections = [Http("billing", "https://billing.local/", "missing-token")] });
        secrets.Invocations.Clear();

        var connections = vm.Connections;
        connections.SelectedAvailable = connections.Available.Single(c => c.Name == "local");
        Execute(connections.AddCommand);
        Execute(connections.ConfirmAddCommand); // adds the driver's package to the draft
        connections.SelectedItem = connections.Items.Single(i => i.Name == "local");
        Execute(connections.RemoveCommand); // and takes it away again

        secrets.Verify(s => s.LoadSnapshot(), Times.Never);
        Assert.Equal("The Secret Store has no secret named 'missing-token'.", connections.Items.Single().Problem);

        vm.SetFrontMatter(new RpadFrontMatter()); // opening the tab again reads the names again
        secrets.Verify(s => s.LoadSnapshot(), Times.Once);
    }

    private sealed class CountingSchemaDriver : IConnectionSchemaDriver
    {
        public int Fetches;
        public string? Failure;
        public string Id => "described";
        public string DisplayName => "Described";
        public ConnectionCategory Category => ConnectionCategory.Grpc;
        public IReadOnlyList<ConnectionParam> Params { get; } = [];
        public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string>();
        public IReadOnlyList<string> Validate(RpadConnection connection) => [];
        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestResult(true, ""));
        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) => new("");
        public Task<ConnectionSchema> FetchSchemaAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, ConnectionTools tools,
            CancellationToken cancellationToken)
        {
            Fetches++;
            return Failure is null ? Task.FromResult(new ConnectionSchema($"{Fetches} services", [], ""))
                : Task.FromException<ConnectionSchema>(new InvalidOperationException(Failure));
        }
    }

    // Refresh reads the selected connection's description again, even one already read, and says what
    // came of it; it is offered only for a connection whose driver reads one.
    [Fact]
    public async Task Refresh_Should_ReadTheSelectedConnectionsDescriptionAgain()
    {
        var folder = Directory.CreateTempSubdirectory("runpad-refresh-schemas").FullName;
        try
        {
            var driver = new CountingSchemaDriver();
            var drivers = new ConnectionDriverRegistry();
            drivers.Register(driver);
            drivers.Register(new HttpConnectionDriver());
            var schemas = new ConnectionSchemas(drivers, folder, new ConnectionTools { GetPackageFolderAsync = (_, _, _) => Task.FromResult("") }, _ => null);
            var described = new RpadConnection { Name = "stock", Driver = "described" };
            await schemas.EnsureAsync([described], null, ConnectionSchemaRefresh.Missing, CancellationToken.None);
            Assert.Equal(1, driver.Fetches);

            var tab = new ScriptConnectionsViewModel(null, null, drivers, schemas);
            tab.Load(new RpadFrontMatter { Connections = [described, Http("billing", "https://billing.local/")] });
            tab.SelectedItem = tab.Items[1];
            Assert.False(tab.CanRefreshSelected);
            Assert.False(((ICommand)tab.RefreshCommand).CanExecute(null));

            tab.SelectedItem = tab.Items[0];
            Assert.True(tab.CanRefreshSelected);
            await tab.RefreshCommand.Execute();
            Assert.Equal(2, driver.Fetches);
            Assert.Equal("'stock' read again: 2 services", tab.RefreshMessage);
            Assert.False(tab.IsRefreshing);

            driver.Failure = "Server unavailable.";
            await tab.RefreshCommand.Execute();
            Assert.Equal("The description of 'stock' couldn't be read: Server unavailable.", tab.RefreshMessage);
        }
        finally { Directory.Delete(folder, true); }
    }

    // Taking a connection out again before saving takes its packages too, unless another needs them.
    [Fact]
    public void RemovingAConnectionAddedThisSession_Should_DropThePackagesItsAddBrought()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "local", Driver = "sqlish" }, new RpadConnection { Name = "other", Driver = "sqlish" });
        var fm = new RpadFrontMatter();
        vm.SetFrontMatter(fm);
        var connections = vm.Connections;
        Execute(connections.AddCommand);
        Execute(connections.ConfirmAddCommand);
        Execute(connections.AddCommand); // "other" needs the same package, already in the draft: no confirmation
        Assert.False(connections.IsAddConfirmationPending);
        Assert.Equal(["local", "other"], connections.Items.Select(i => i.Name));

        connections.SelectedItem = connections.Items[0];
        Execute(connections.RemoveCommand);
        Assert.Null(connections.Items.Single().Problem); // "other" still needs the package
        Execute(connections.RemoveCommand);
        Assert.Empty(connections.Items);
        Assert.False(vm.HasChanges); // the package draft is back to the script's: none
    }

    [Fact]
    public void RemovingAConnection_Should_KeepPackagesTheScriptAlreadyHad()
    {
        var (vm, _, _) = CreateVm(new RpadConnection { Name = "local", Driver = "sqlish" });
        var fm = new RpadFrontMatter { Packages = new() { ["Microsoft.Data.Sqlite"] = new Package("8.0.0") } };
        vm.SetFrontMatter(fm);
        Execute(vm.Connections.AddCommand);
        Assert.Null(vm.Connections.Items.Single().Problem);
        Execute(vm.Connections.RemoveCommand);
        Assert.False(vm.HasChanges); // the script's own package is still in the draft

    }

    [Fact]
    public void AFailedImportWrite_Should_AllowAnotherTry_ButAnUnreadableListNot()
    {
        var (vm, store, mine) = CreateVm();
        vm.SetFrontMatter(new RpadFrontMatter { Connections = [Http("orders", "https://orders/")] });
        var connections = vm.Connections;
        connections.SelectedItem = connections.Items[0];
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>())).Throws(new IOException("disk full"));
        Execute(connections.ImportCommand);
        Assert.Contains("could not be saved", connections.StoreError);
        Assert.False(connections.HasReadError);

        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()))
            .Callback<IReadOnlyList<RpadConnection>, string?>((saved, _) => { mine.Clear(); mine.AddRange(saved); })
            .Returns("v2");
        Execute(connections.ImportCommand);
        Assert.Null(connections.StoreError);
        Assert.Equal(["orders"], mine.Select(c => c.Name));

        store.Setup(s => s.LoadSnapshot()).Throws(new InvalidDataException("The connection list file is damaged."));
        vm.SetFrontMatter(new RpadFrontMatter { Connections = [Http("orders", "https://orders/")] });
        connections.SelectedItem = connections.Items[0];
        Assert.True(connections.HasReadError);
        Assert.False(((ICommand)connections.ImportCommand).CanExecute(null));
    }

    [Fact]
    public void TheChosenConnection_Should_StayChosen_WhenTheListIsRefreshed()
    {
        var (vm, _, _) = CreateVm(Http("a", "https://a/"), Http("b", "https://b/"), Http("c", "https://c/"));
        vm.SetFrontMatter(new RpadFrontMatter());
        var connections = vm.Connections;
        connections.SelectedAvailable = connections.Available[1];
        connections.OnPackagesChanged();
        Assert.Equal("b", connections.SelectedAvailable?.Name);
        Execute(connections.AddCommand);
        Assert.Equal("a", connections.SelectedAvailable?.Name); // b was added: the first one left
    }

    // orders-db and OrdersDb are both Connections.OrdersDb: one script can't list both.
    [Fact]
    public void ConnectionsWithTheSameCodeName_Should_CountAsTheSameConnection()
    {
        var (vm, _, mine) = CreateVm(Http("orders-db", "https://old/"), Http("github", "https://api.github.com/"));
        vm.SetFrontMatter(new RpadFrontMatter { Connections = [Http("OrdersDb", "https://new/")] });
        var connections = vm.Connections;
        Assert.Equal(["github"], connections.Available.Select(c => c.Name));
        var item = Assert.Single(connections.Items);
        Assert.Equal("Has the same name in code as your connection 'orders-db': Import replaces yours.", item.Status);

        connections.SelectedItem = item;
        Execute(connections.ImportCommand);
        Assert.True(connections.IsImportConfirmationPending);
        Assert.StartsWith("Replace your saved connection 'orders-db' with 'OrdersDb'?", connections.ImportMessage);
        Assert.Equal("orders-db", mine[0].Name);
        Execute(connections.ConfirmImportCommand);
        Assert.Equal(["OrdersDb", "github"], mine.Select(c => c.Name));
        Assert.Equal("Same as in your connections.", item.Status);
    }

    [Fact]
    public void ListProblems_Should_ShowOnTheConnectionsTheyConcern()
    {
        var (vm, _, _) = CreateVm();
        vm.SetFrontMatter(new RpadFrontMatter
        {
            Connections = [Http("orders", "https://a/"), Http("Orders", "https://b/"), Http("connections", "https://c/"), Http("bad name", "https://d/")],
        });
        Assert.Equal(
            [
                null,
                "Two connections are named 'Orders'.",
                "Connection 'connections': the code name Connections is reserved.",
                "'bad name' is not a valid connection name: start with a letter, then use letters, digits, '-', '_' or '.'.",
            ],
            vm.Connections.Items.Select(i => i.Problem));
    }
}
