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
        var list = mine.ToList();
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.LoadSnapshot()).Returns(() => new ConnectionStoreSnapshot([.. list], null));
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>(), It.IsAny<string?>()))
            .Callback<IReadOnlyList<RpadConnection>, string?>((saved, _) => { list.Clear(); list.AddRange(saved); });
        var secrets = new Mock<ISecretStore>();
        secrets.Setup(s => s.LoadSnapshot()).Returns(new SecretStoreSnapshot([new Secret("billing-token", "t0k")], null));
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
        Assert.Null(vm.Connections.Items.Single().Problem);
        Assert.True(vm.ApplyChanges());
        Assert.Equal("9.0.0", fm.Packages["Microsoft.Data.Sqlite"].Version);

        var (other, _, _) = CreateVm();
        other.SetFrontMatter(new RpadFrontMatter { Connections = [new RpadConnection { Name = "local", Driver = "sqlish" }] });
        Assert.Equal("Needs the NuGet package Microsoft.Data.Sqlite: add it on the Packages tab.", other.Connections.Items.Single().Problem);
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
        Execute(connections.AddCommand);
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
