using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Inventory.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Connectors.Grpc;
using RunPad.Contracts.Connections;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Runtime.UI.Tests;
using RunPad.Services.Secrets;
using RunPad.Windows.Models;
using Xunit;

namespace RunPad.NonUI.Tests;

// A gRPC connection reads the server's services through server reflection, has protoc write their
// classes, and gives the script a client per service that sends the connection's metadata. The
// server here is a real one on a loopback port. RUNPAD_SECRETS_FILE is set here, so these share the
// real-build collection with the other tests that set it.
[Collection(RealBuildTestCollection.Name)]
public sealed class GrpcConnectionTests : IAsyncLifetime
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-grpc-connection").FullName;
    private WebApplication? _server;
    private WebApplication? _plainServer;

    private string Address { get; set; } = "";

    // A server without reflection, for the error a user sees then.
    private string PlainAddress { get; set; } = "";

    private sealed class StockService : Stock.StockBase
    {
        public override Task<Item> GetItem(ItemRequest request, ServerCallContext context) => Task.FromResult(new Item
        {
            Sku = request.Sku,
            Count = 42,
            Caller = context.RequestHeaders.GetValue("authorization") ?? "",
        });
    }

    private sealed class AuditService : Audit.AuditBase
    {
        public override Task<ItemRequest> Ping(ItemRequest request, ServerCallContext context) => Task.FromResult(request);
    }

    private static async Task<(WebApplication, string)> StartAsync(bool reflection)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        if (reflection) builder.Services.AddGrpcReflection();
        var app = builder.Build();
        app.MapGrpcService<StockService>();
        app.MapGrpcService<AuditService>();
        if (reflection) app.MapGrpcReflectionService();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, address.Replace("[::1]", "127.0.0.1", StringComparison.Ordinal));
    }

    public async ValueTask InitializeAsync()
    {
        (_server, Address) = await StartAsync(reflection: true);
        (_plainServer, PlainAddress) = await StartAsync(reflection: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (_plainServer is not null) await _plainServer.DisposeAsync();
        Directory.Delete(_folder, true);
    }

    private RpadConnection Inventory(string? address = null, string? metadata = "Bearer ${secret:inventory-token}") => new()
    {
        Name = "inventory",
        Driver = "grpc",
        Params = metadata is null
            ? new(StringComparer.Ordinal) { ["address"] = RpadValue.Of(address ?? Address) }
            : new(StringComparer.Ordinal)
            {
                ["address"] = RpadValue.Of(address ?? Address),
                ["metadata"] = RpadValue.Map([new("authorization", RpadValue.Of(metadata))]),
            },
    };

    private static Dictionary<string, RpadValue> Resolved(RpadConnection connection) =>
        ConnectionPlaceholderResolver.Resolve(connection.Params, _ => "t0k", _ => null).Params.ToDictionary();

    private ConnectionSchemas Schemas()
    {
        var drivers = new ConnectionDriverRegistry();
        drivers.Register(new GrpcConnectionDriver());
        return new ConnectionSchemas(drivers, Path.Combine(_folder, "schemas"), DotnetConnectionTools.Default,
            name => name == "inventory-token" ? "t0k" : null);
    }

    [Fact]
    public void Validate_Should_NameWhatIsWrong()
    {
        var driver = new GrpcConnectionDriver();
        Assert.Empty(driver.Validate(Inventory("https://${env:INVENTORY_HOST}")));
        Assert.Equal(["The address is missing."], driver.Validate(new RpadConnection { Name = "a", Driver = "grpc" }));
        var bad = new RpadConnection
        {
            Name = "a",
            Driver = "grpc",
            Params = new(StringComparer.Ordinal)
            {
                ["address"] = RpadValue.Of("localhost:5001"),
                ["metadata"] = RpadValue.Map([new("token-bin", RpadValue.Of("x"))]),
                ["skipCertificateCheck"] = RpadValue.Of("yes"),
            },
        };
        Assert.Equal(
        [
            "The address 'localhost:5001' is not an http:// or https:// address.",
            "'token-bin' can't be a metadata name: use letters, digits, '-', '_' and '.', and no '-bin' ending.",
            "Skip the certificate check must be true or false, not 'yes'.",
        ], driver.Validate(bad));
    }

    [Fact]
    public async Task Test_Should_ListTheServices_OrSayWhyNot()
    {
        var driver = new GrpcConnectionDriver();
        var ok = await driver.TestAsync(Resolved(Inventory()), TestContext.Current.CancellationToken);
        Assert.True(ok.Succeeded, ok.Message);
        Assert.Equal("2 services: inventory.v1.Audit, inventory.v1.Stock.", ok.Message);

        var plain = await driver.TestAsync(Resolved(Inventory(PlainAddress)), TestContext.Current.CancellationToken);
        Assert.False(plain.Succeeded);
        Assert.Equal("The server doesn't offer gRPC server reflection, which RunPad reads its services with.", plain.Message);

        var closed = await driver.TestAsync(Resolved(Inventory("http://127.0.0.1:1")), TestContext.Current.CancellationToken);
        Assert.False(closed.Succeeded);
        Assert.StartsWith("Could not reach the server:", closed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceProperties_Should_BeShort_AndUnique()
    {
        var clients = GrpcClientParser.WithProperties(
        [
            new("inventory.v1.Stock", "global::A.Stock.StockClient", ""),
            new("legacy.Stock", "global::B.Stock.StockClient", ""),
            new("inventory.v1.Audit", "global::A.Audit.AuditClient", ""),
            new("x.channel", "global::C.channel.channelClient", ""),
        ]);
        Assert.Equal(["InventoryV1Stock", "LegacyStock", "Audit", "XChannel"], clients.Select(c => c.Property));
    }

    // Reading the schema needs Grpc.Tools from NuGet; the script's build needs the SDK.
    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task AScript_Should_CallTheServer_ThroughItsGeneratedClient()
    {
        if (!new DotnetSdkProvider().Get().Any()) return;
        var connection = Inventory();
        var schemas = Schemas();
        Assert.True(await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken));
        Assert.Null(schemas.Cache.ErrorFor(connection));
        var schema = schemas.Cache.Get(connection)!;
        Assert.Equal("2 services: inventory.v1.Audit, inventory.v1.Stock.", schema.Summary);
        Assert.Contains(schema.Files, f => f.Name == "Inventory.cs");
        Assert.Contains(schema.Files, f => f.Name == "InventoryGrpc.cs");
        Assert.DoesNotContain(schema.Files, f => f.Name.Contains("Timestamp", StringComparison.Ordinal)); // Google.Protobuf has it
        // Read once: nothing changes the next time.
        Assert.False(await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken));

        var packages = new GrpcConnectionDriver().Packages;
        var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp, packages.Keys, schemas.Drivers, schemas.Cache)!;
        Assert.Empty(generated.Errors);
        Assert.Contains("public global::Inventory.V1.Stock.StockClient Stock => __stock ??= new global::Inventory.V1.Stock.StockClient(CallInvoker);",
            generated.Source);
        Assert.Contains(generated.Files, f => f.Name == "Inventory.InventoryGrpc.cs");

        // The editor knows the services' clients and messages before any run.
        var referenceService = new CSharpReferenceService();
        using (var service = new CSharpIntelliSenseService(
                   referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance))
        {
            service.UpdateExecutionContext(new ExecutionRequest { Packages = packages, Connections = generated });
            const string code = "Connections.Inventory.";
            var members = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);
            Assert.Contains(members!.Items, i => i.DisplayText == "Stock");
            Assert.Contains(members.Items, i => i.DisplayText == "Audit");
        }

        var secretsFile = Path.Combine(_folder, "secrets.json");
        new FileSecretStore(secretsFile).Save([new Secret("inventory-token", "t0k")], null);
        var previousSecrets = Environment.GetEnvironmentVariable(FileSecretStore.FileEnvVar);
        Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, secretsFile);
        try
        {
            var outputDir = Path.Combine(_folder, "build");
            Directory.CreateDirectory(outputDir);
            var options = CompiledDotnetLanguageHostOptions.From(new ExecutionRequest
            {
                Platform = new DotnetSdkProvider().Get().First(),
                OutputDirectory = outputDir,
                Packages = packages,
                Connections = generated,
            }, new CSharpReferenceService().GetExtensionReference());
            await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
            var output = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            const string script = """
                var item = await Connections.Inventory.Stock.GetItemAsync(new Inventory.V1.ItemRequest { Sku = "A1" });
                Console.WriteLine($"{item.Sku} {item.Count} {item.Caller}");
                """;
            var result = await host.RunAsync(script, (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data),
                    TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, string.Join('\n', errors));
            Assert.Equal("A1 42 Bearer t0k", output.Last());
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, previousSecrets);
        }
    }

    // A server that can't be read is a build error with the reason, and isn't asked again in the
    // background; a run asks again.
    [Fact]
    public async Task AnUnreadableServer_Should_BeABuildError_WithTheReason()
    {
        var connection = Inventory(PlainAddress, metadata: null);
        var schemas = Schemas();
        var packages = new GrpcConnectionDriver().Packages.Keys;
        Assert.Equal(["Connection 'inventory': its description hasn't been read yet; RunPad reads it when the script is opened or run."],
            GeneratedConnections.For([connection], RpadLanguage.CSharp, packages, schemas.Drivers, schemas.Cache)!.Errors);

        Assert.True(await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken));
        Assert.Equal(
            ["Connection 'inventory': its description couldn't be read: The server doesn't offer gRPC server reflection, which RunPad reads its services with."],
            GeneratedConnections.For([connection], RpadLanguage.CSharp, packages, schemas.Drivers, schemas.Cache)!.Errors);
        Assert.False(await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken));
        Assert.True(await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Failed, TestContext.Current.CancellationToken));

        // Dependencies → Connections says so too.
        var tab = new ScriptConnectionsViewModel(null, null, schemas.Drivers, schemas.Cache);
        tab.UsePackages(() => new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase), (_, _) => { });
        tab.Load(new RpadFrontMatter { Connections = [connection] });
        Assert.Equal("Its description couldn't be read: The server doesn't offer gRPC server reflection, which RunPad reads its services with.",
            Assert.Single(tab.Items).Problem);

        // A missing secret is such a reason too.
        var secretless = Inventory(metadata: "Bearer ${secret:nope}");
        await schemas.EnsureAsync([secretless], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken);
        Assert.Equal("The Secret Store has no secret named 'nope'. Add it in Tools → Secret Store.", schemas.Cache.ErrorFor(secretless));
    }
}
