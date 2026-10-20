using System.Reactive.Linq;
using System.Windows.Input;
using Moq;
using RunPad.Connectors.Http;
using RunPad.Contracts.Connections;
using RunPad.Format.RPad;
using RunPad.Services.Connections;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class FileConnectionStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-connections-tests").FullName;
    private string StorePath => Path.Combine(_folder, "nested", "connections.json");

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void SavedConnections_Should_LoadBack_WithTheirParams()
    {
        var store = new FileConnectionStore(StorePath);
        Assert.Empty(store.Load());
        store.Save(
        [
            new RpadConnection
            {
                Name = "billing",
                Driver = "http",
                Params = new(StringComparer.Ordinal)
                {
                    ["baseAddress"] = RpadValue.Of("https://billing.local/"),
                    ["headers"] = RpadValue.Map([new("Authorization", RpadValue.Of("Bearer ${secret:billing}"))]),
                },
            },
        ]);
        var loaded = Assert.Single(new FileConnectionStore(StorePath).Load());
        Assert.Equal(("billing", "http"), (loaded.Name, loaded.Driver));
        Assert.Equal(["baseAddress", "headers"], loaded.Params.Keys);
        Assert.Equal("Bearer ${secret:billing}", loaded.Params["headers"].Fields!["Authorization"].Text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Version\":2,\"Connections\":[]}")]
    public void AListThatCantBeRead_Should_FailToLoad_AndStayUnchanged(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, content);
        Assert.Throws<InvalidDataException>(() => new FileConnectionStore(StorePath).Load());
        Assert.Equal(content, File.ReadAllText(StorePath));
    }
}

// The Connections window without a window: the form follows the driver's parameters, edits are
// saved only by Save (the window's OK), and Test uses the Secret Store's values.
public class ConnectionsWindowViewModelTests
{
    private sealed class FakeDriver : IConnectionDriver
    {
        public IReadOnlyDictionary<string, RpadValue>? Tested { get; private set; }
        public string Id => "fake";
        public string DisplayName => "Fake";
        public ConnectionCategory Category => ConnectionCategory.Database;

        public IReadOnlyList<ConnectionParam> Params { get; } =
        [
            new("connectionString", "Connection string", Required: true),
            new("pooling", "Pooling", ConnectionParamType.Boolean, Default: "true", Advanced: true),
            new("mode", "Mode", ConnectionParamType.Choice, Choices: ["ReadOnly", "ReadWrite"]),
            new("schemas", "Schemas", ConnectionParamType.List),
        ];

        public IReadOnlyList<string> Validate(RpadConnection connection) =>
            connection.Params.ContainsKey("connectionString") ? [] : ["The connection string is missing."];

        public Task<ConnectionTestResult> TestAsync(IReadOnlyDictionary<string, RpadValue> resolvedParams, CancellationToken cancellationToken)
        {
            Tested = resolvedParams;
            return Task.FromResult(new ConnectionTestResult(true, "Connected."));
        }

        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) =>
            new($"public sealed class {context.ClassName} {{ internal {context.ClassName}() {{ }} }}");
    }

    private static readonly RpadConnection Billing = new()
    {
        Name = "billing",
        Driver = "http",
        Params = new(StringComparer.Ordinal)
        {
            ["baseAddress"] = RpadValue.Of("https://billing.local/"),
            ["headers"] = RpadValue.Map([new("Authorization", RpadValue.Of("Bearer ${secret:billing}"))]),
            ["retries"] = RpadValue.Of("3"), // not an http parameter: kept as written
        },
    };

    private static readonly RpadConnection Future = new()
    {
        Name = "warehouse",
        Driver = "snowflake",
        Params = new(StringComparer.Ordinal) { ["account"] = RpadValue.Of("acme") },
    };

    private static (ConnectionsWindowViewModel Vm, Mock<IConnectionStore> Store, FakeDriver Fake) CreateVm(
        IReadOnlyList<Secret>? secrets = null, params RpadConnection[] connections)
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.Load()).Returns(connections);
        store.Setup(s => s.FilePath).Returns("/data/RunPad/connections.json");
        var secretStore = new Mock<ISecretStore>();
        secretStore.Setup(s => s.Load()).Returns(secrets ?? [new Secret("billing", "t0k"), new Secret("db", "pw")]);
        var registry = new ConnectionDriverRegistry();
        registry.Register(new HttpConnectionDriver());
        var fake = new FakeDriver();
        registry.Register(fake);
        return (new ConnectionsWindowViewModel(store.Object, registry, secretStore.Object), store, fake);
    }

    private static void Execute(ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter));
        command.Execute(parameter);
    }

    [Fact]
    public void AConnection_Should_ShowAFieldPerDriverParameter()
    {
        var (vm, _, _) = CreateVm(null, Billing, Future);
        var billing = vm.SelectedConnection!;
        Assert.Equal("HTTP", billing.DriverName);
        Assert.Equal(["baseAddress", "headers", "timeout"], billing.Fields.Select(f => f.Param.Key));
        Assert.Equal("https://billing.local/", billing.Fields[0].Text);
        Assert.Equal(("Authorization", "Bearer ${secret:billing}"), (billing.Fields[1].Rows[0].Name, billing.Fields[1].Rows[0].Value));
        Assert.Equal("Also kept as written: retries.", billing.KeptParamsNote);
        Assert.Null(billing.Problems);

        var future = vm.Connections[1];
        Assert.Equal("snowflake (not available in this RunPad)", future.DriverName);
        Assert.Empty(future.Fields);
        Assert.False(((ICommand)vm.TestCommand).CanExecute(null) && vm.SelectedConnection == future);
    }

    [Fact]
    public void Edits_Should_BeSavedOnlyBySave_InTheDriversOrder()
    {
        var (vm, store, fake) = CreateVm(null, Billing, Future);
        Execute(vm.AddCommand, fake);
        var added = vm.SelectedConnection!;
        Assert.Equal("fake", added.Name);
        Assert.Equal(["connectionString", "mode", "schemas", "pooling"], added.Fields.Select(f => f.Param.Key)); // advanced last
        Assert.Equal("The connection string is missing.", added.Problems);

        added.Fields[0].SecretToInsert = "db";
        added.Fields[0].Text = "Data Source=x;Password=" + "${secret:db}";
        added.Fields[1].Text = "ReadOnly";
        added.Fields[2].Text = "public\n\n sales \n";
        added.Fields[3].BooleanValue = false;
        Assert.Null(added.Problems);

        var billing = vm.Connections[0];
        Execute(billing.Fields[1].AddRowCommand);
        billing.Fields[1].Rows[1].Name = "Accept";
        billing.Fields[1].Rows[1].Value = "application/json";
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>()), Times.Never);

        IReadOnlyList<RpadConnection>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>())).Callback<IReadOnlyList<RpadConnection>>(list => saved = list);
        Assert.True(vm.Save());

        Assert.NotNull(saved);
        Assert.Equal(["billing", "warehouse", "fake"], saved.Select(c => c.Name));
        Assert.Equal(["baseAddress", "headers", "retries"], saved[0].Params.Keys);
        Assert.Equal(["Authorization", "Accept"], saved[0].Params["headers"].Fields!.Keys);
        Assert.Equal("acme", saved[1].Params["account"].Text); // a driver this RunPad lacks: params kept
        Assert.Equal(["connectionString", "pooling", "mode", "schemas"], saved[2].Params.Keys);
        Assert.Equal("Data Source=x;Password=${secret:db}", saved[2].Params["connectionString"].Text);
        Assert.Equal("false", saved[2].Params["pooling"].Text);
        Assert.Equal(["public", "sales"], saved[2].Params["schemas"].Items!.Select(i => i.Text));
    }

    [Fact]
    public void ASwitchLeftAtItsDefault_AndEmptyFields_Should_NotBeWritten()
    {
        var (vm, store, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        vm.SelectedConnection!.Fields[0].Text = "Data Source=x";
        IReadOnlyList<RpadConnection>? saved = null;
        store.Setup(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>())).Callback<IReadOnlyList<RpadConnection>>(list => saved = list);
        Assert.True(vm.Save());
        Assert.Equal(["connectionString"], Assert.Single(saved!).Params.Keys);
    }

    [Fact]
    public void BadNames_Should_BlockSaving_ButAnUnfinishedConnectionNot()
    {
        var (vm, store, fake) = CreateVm(null, Billing);
        Execute(vm.AddCommand, fake);
        Assert.True(vm.CanSave); // the connection string is still missing: a problem, not a block

        vm.SelectedConnection!.Name = "Billing";
        Assert.Equal("Two connections are named 'Billing'.", vm.ValidationError);
        Assert.False(vm.Save());
        vm.SelectedConnection.Name = "2nd";
        Assert.False(vm.CanSave);
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>()), Times.Never);

        Execute(vm.RemoveCommand);
        Assert.True(vm.CanSave);
        Assert.Equal("billing", vm.SelectedConnection?.Name);
    }

    [Fact]
    public void AddedNames_Should_NotRepeat()
    {
        var (vm, _, _) = CreateVm(null, Billing, new RpadConnection { Name = "http", Driver = "http" });
        var http = vm.Drivers.First(d => d.Id == "http");
        Execute(vm.AddCommand, http);
        Execute(vm.AddCommand, http);
        Assert.Equal(["billing", "http", "http2", "http3"], vm.Connections.Select(c => c.Name));
    }

    [Fact]
    public async Task Test_Should_UseTheSecretStore_AndReportMissingSecrets()
    {
        var (vm, _, fake) = CreateVm();
        Execute(vm.AddCommand, fake);
        var item = vm.SelectedConnection!;
        item.Fields[0].Text = "Password=${secret:DB}";
        await vm.TestCommand.Execute();
        Assert.Equal("✓ Connected.", item.TestResult);
        Assert.Equal("Password=pw", fake.Tested!["connectionString"].Text);

        item.Fields[0].Text = "Password=${secret:missing}";
        Assert.Null(item.TestResult); // an edit clears the last result
        await vm.TestCommand.Execute();
        Assert.Equal("The Secret Store has no secret named 'missing'. Add it in Tools → Secret Store.", item.TestResult);
    }

    [Fact]
    public void AListThatCantBeRead_Should_BeReported_AndNeverOverwritten()
    {
        var store = new Mock<IConnectionStore>();
        store.Setup(s => s.Load()).Throws(new InvalidDataException("The connection list file is damaged."));
        store.Setup(s => s.FilePath).Returns("/data/RunPad/connections.json");
        var vm = new ConnectionsWindowViewModel(store.Object, new ConnectionDriverRegistry(),
            Mock.Of<ISecretStore>(secrets => secrets.Load() == Array.Empty<Secret>()));
        Assert.Contains("damaged", vm.LoadError);
        Assert.False(vm.Save());
        store.Verify(s => s.Save(It.IsAny<IReadOnlyList<RpadConnection>>()), Times.Never);
    }
}
