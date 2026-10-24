using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
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
            Caller = string.Join(",", context.RequestHeaders.Where(h => h.Key == "authorization").Select(h => h.Value)),
        });
    }

    private sealed class AuditService : Audit.AuditBase
    {
        public override Task<ItemRequest> Ping(ItemRequest request, ServerCallContext context) => Task.FromResult(request);
    }

    private static async Task<(WebApplication, string)> StartAsync(bool reflection, HttpProtocols protocols = HttpProtocols.Http2,
        X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l =>
        {
            l.Protocols = protocols;
            if (certificate is not null) l.UseHttps(certificate);
        }));
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

    // The Test button gets the parameters unchecked: a name Metadata refuses is a message, not a crash.
    [Theory]
    [InlineData("bad key")]
    [InlineData("token-bin")]
    public async Task Test_Should_SayWhichMetadataNameIsInvalid(string name)
    {
        var parameters = new Dictionary<string, RpadValue>(StringComparer.Ordinal)
        {
            ["address"] = RpadValue.Of(Address),
            ["metadata"] = RpadValue.Map([new(name, RpadValue.Of("x"))]),
        };
        var result = await new GrpcConnectionDriver().TestAsync(parameters, TestContext.Current.CancellationToken);
        Assert.Equal(new ConnectionTestResult(false,
            $"'{name}' can't be a metadata name: use letters, digits, '-', '_' and '.', and no '-bin' ending."), result);
    }

    // The client sends the connection's metadata in a Metadata of its own for each call, so the one a
    // script passes and reuses is left as it was.
    [Fact]
    public void TheClient_Should_NeverAddToTheCallersMetadata()
    {
        var connection = Inventory();
        var schemas = Schemas();
        schemas.Cache.Put(connection, new ConnectionSchema("", [], "[]"));
        var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp,
            new GrpcConnectionDriver().Packages.Keys, schemas.Drivers, schemas.Cache)!;
        Assert.Empty(generated.Errors);
        Assert.Contains("var all = new global::Grpc.Core.Metadata();", generated.Source, StringComparison.Ordinal);
        Assert.Contains("if (headers is not null) foreach (var entry in headers) all.Add(entry);", generated.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("headers.Add(", generated.Source, StringComparison.Ordinal);
    }

    // Files of the same name in different packages (x/v1/service.proto, x/v2/service.proto) are
    // written to folders of their own instead of over each other. Grpc.Tools comes from NuGet.
    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task Protoc_Should_WriteFilesOfTheSameName_FromDifferentPackages()
    {
        static Google.Protobuf.Reflection.FileDescriptorProto Proto(string version)
        {
            var file = new Google.Protobuf.Reflection.FileDescriptorProto
            {
                Name = $"x/{version}/service.proto",
                Package = $"x.{version}",
                Syntax = "proto3",
            };
            file.MessageType.Add(new Google.Protobuf.Reflection.DescriptorProto { Name = "Ping" });
            var service = new Google.Protobuf.Reflection.ServiceDescriptorProto { Name = "Echo" };
            service.Method.Add(new Google.Protobuf.Reflection.MethodDescriptorProto
            {
                Name = "Send",
                InputType = $".x.{version}.Ping",
                OutputType = $".x.{version}.Ping",
            });
            file.Service.Add(service);
            return file;
        }

        var tools = await DotnetConnectionTools.Default.GetPackageFolderAsync("Grpc.Tools", GrpcConnectionDriver.GrpcVersion,
            TestContext.Current.CancellationToken);
        var files = await GrpcConnectionDriver.ProtocAsync(tools, [Proto("v1"), Proto("v2")], TestContext.Current.CancellationToken);
        Assert.Equal(["X/V1/Service.cs", "X/V1/ServiceGrpc.cs", "X/V2/Service.cs", "X/V2/ServiceGrpc.cs"], files.Select(f => f.Name));
        Assert.All(files, f => Assert.Contains("#nullable enable", f.Text, StringComparison.Ordinal));
        var clients = GrpcClientParser.Find(files, ["x.v1.Echo", "x.v2.Echo"]);
        Assert.Equal(["global::X.V1.Echo.EchoClient", "global::X.V2.Echo.EchoClient"], clients.Select(c => c.ClientType));
        Assert.Equal(["XV1Echo", "XV2Echo"], GrpcClientParser.WithProperties(clients).Select(c => c.Property));
    }

    // protoc's files are <auto-generated>, where nullable is off: the directive goes after the header
    // comment, whatever the line endings, and the code below it is left as it was.
    [Theory]
    [InlineData("// <auto-generated>\n//   x\n// </auto-generated>\n#pragma warning disable 1591\nclass A { }\n",
        "// <auto-generated>\n//   x\n// </auto-generated>\n#nullable enable\n#pragma warning disable 1591\nclass A { }\n")]
    [InlineData("// <auto-generated>\r\n// </auto-generated>\r\nclass A { }", "// <auto-generated>\r\n// </auto-generated>\r\n#nullable enable\r\nclass A { }")]
    [InlineData("class A { }", "#nullable enable\nclass A { }")]
    [InlineData("", "#nullable enable\n")]
    public void GeneratedFiles_Should_GetTheNullableDirective_AfterTheirHeader(string source, string expected) =>
        Assert.Equal(expected, GrpcConnectionDriver.WithNullableContext(source));

    // The generated wrapper is compiled as generated code too, and has annotations: CS8669 is only
    // avoided by the directive in its own text, not by switching the warning off.
    [Fact]
    public void ConnectionWrapper_Should_EnableNullable_ForItsOwnText()
    {
        var generated = GeneratedConnections.For([Inventory()], RpadLanguage.CSharp, new GrpcConnectionDriver().Packages.Keys, Schemas().Drivers,
            Schemas().Cache);
        Assert.NotNull(generated);
        Assert.StartsWith("#nullable enable", generated.Source, StringComparison.Ordinal);
        Assert.EndsWith("#nullable restore\n", generated.Source, StringComparison.Ordinal);
    }

    // The server names the files RunPad passes to protoc: a name must never become one of its options.
    [Theory]
    [InlineData("inventory.proto", true)]
    [InlineData("inventory/v1/stock.proto", true)]
    [InlineData("google/protobuf/timestamp.proto", true)]
    [InlineData("--plugin=protoc-gen-grpc=/bin/sh", false)]
    [InlineData("--descriptor_set_out=/home/u/.bashrc.proto", false)]
    [InlineData("@args.proto", false)]
    [InlineData("/etc/passwd.proto", false)]
    [InlineData("../outside.proto", false)]
    [InlineData("a//b.proto", false)]
    [InlineData("inventory.txt", false)]
    public void FileNames_FromTheServer_Should_BeChecked(string name, bool safe) =>
        Assert.Equal(safe, GrpcReflection.IsSafeProtoPath(name));

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

    [Fact]
    public void SharedSchema_Should_CompileOnce_AndRejectConflictingDefinitions()
    {
        var connection = Inventory();
        var other = Inventory();
        other.Name = "other";
        other.Params["address"] = RpadValue.Of("http://localhost:5002");
        var schemas = Schemas();
        const string code = "namespace Shared { public partial class Message { public void Send() {} } }";
        schemas.Cache.Put(connection, new ConnectionSchema("", [new("Shared.cs", code)], "[]"));
        schemas.Cache.Put(other, new ConnectionSchema("", [new("Shared.cs", code)], "[]"));
        var generated = GeneratedConnections.For([connection, other], RpadLanguage.CSharp,
            new GrpcConnectionDriver().Packages.Keys, schemas.Drivers, schemas.Cache)!;
        Assert.Empty(generated.Errors);
        Assert.Single(generated.Files);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("SharedSchema",
            generated.Files.Select(f => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(f.Text)),
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        schemas.Cache.Put(other, new ConnectionSchema("", [new("Shared.cs", code.Replace("Send", "Receive"))], "[]"));
        generated = GeneratedConnections.For([connection, other], RpadLanguage.CSharp,
            new GrpcConnectionDriver().Packages.Keys, schemas.Drivers, schemas.Cache)!;
        Assert.Contains(generated.Errors, e => e.Contains("conflicts with another connection", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceProperties_Should_AvoidTheActualWrapperClassName()
    {
        var connection = Inventory();
        var schemas = Schemas();
        schemas.Cache.Put(connection, new ConnectionSchema("", [],
            "[{\"Service\":\"example.InventoryClient\",\"ClientType\":\"global::Example.Client\",\"Property\":\"InventoryClient\"}]"));
        var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp,
            new GrpcConnectionDriver().Packages.Keys, schemas.Drivers, schemas.Cache)!;
        Assert.Empty(generated.Errors);
        Assert.Contains("public global::Example.Client ExampleInventoryClient =>", generated.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("public global::Example.Client InventoryClient =>", generated.Source, StringComparison.Ordinal);
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
        Assert.Contains(schema.Files, f => f.Name == "Inventory/V1/Inventory.cs");
        Assert.Contains(schema.Files, f => f.Name == "Inventory/V1/InventoryGrpc.cs");
        Assert.DoesNotContain(schema.Files, f => f.Name.Contains("Timestamp", StringComparison.Ordinal)); // Google.Protobuf has it
        // Read once: nothing changes the next time.
        Assert.False(await schemas.EnsureAsync([connection], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken));

        var packages = new GrpcConnectionDriver().Packages;
        var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp, packages.Keys, schemas.Drivers, schemas.Cache)!;
        Assert.Empty(generated.Errors);
        Assert.Contains("public global::Inventory.V1.Stock.StockClient Stock => __stock ??= new global::Inventory.V1.Stock.StockClient(CallInvoker);",
            generated.Source);
        Assert.Contains(generated.Files, f => f.Name == "Inventory.Inventory/V1/InventoryGrpc.cs");

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
            // The caller's own metadata goes too, and is the same after each call.
            const string script = """
                var headers = new Grpc.Core.Metadata { { "x-trace", "1" } };
                await Connections.Inventory.Stock.GetItemAsync(new Inventory.V1.ItemRequest { Sku = "A0" }, headers);
                var item = await Connections.Inventory.Stock.GetItemAsync(new Inventory.V1.ItemRequest { Sku = "A1" }, headers);
                Console.WriteLine($"{item.Sku} {item.Count} {item.Caller} {headers.Count}");
                """;
            var result = await host.RunAsync(script, (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data),
                    TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, string.Join('\n', errors));
            Assert.DoesNotContain(output.Concat(errors), line => line.Contains("CS8669", StringComparison.Ordinal));
            Assert.Equal("A1 42 Bearer t0k 1", output.Last());
            var contract = Assert.Single(Directory.GetFiles(Path.Combine(outputDir, "obj", "connection-contracts"), "*.dll"));
            var contractTime = File.GetLastWriteTimeUtc(contract);
            Assert.True(File.Exists(Path.Combine(outputDir, "bin", Path.GetFileName(contract))));
            const string editedScript = script + "\n// A script edit must not rebuild the contract.";
            var second = await host.RunAsync(editedScript,
                (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data), TestContext.Current.CancellationToken);
            Assert.True(second.Succeeded, string.Join('\n', errors));
            Assert.Equal(contractTime, File.GetLastWriteTimeUtc(contract));
            var binary = Path.Combine(outputDir, "bin", "RunPadQuery.dll");
            var binaryTime = File.GetLastWriteTimeUtc(binary);
            var runtimeContract = Path.Combine(outputDir, "bin", Path.GetFileName(contract));
            File.Delete(runtimeContract);
            var hotRun = await host.RunAsync(editedScript,
                (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data), TestContext.Current.CancellationToken);
            Assert.True(hotRun.Succeeded, string.Join('\n', errors));
            Assert.True(File.Exists(runtimeContract));
            Assert.Equal(binaryTime, File.GetLastWriteTimeUtc(binary));
            using var cachedEditor = new CSharpIntelliSenseService(
                referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance);
            cachedEditor.UpdateExecutionContext(new ExecutionRequest
            {
                Platform = options.Platform,
                OutputDirectory = outputDir,
                Packages = packages,
                Connections = generated,
            });
            const string cachedCode = "Connections.Inventory.Stock.";
            var cachedMembers = await cachedEditor.GetCompletionsAsync(cachedCode, cachedCode.Length, ct: TestContext.Current.CancellationToken);
            Assert.Contains(cachedMembers!.Items, i => i.DisplayText == "GetItemAsync");
            const string messageType = "var request = new Inventory.V1.ItemRequest();";
            var definition = await cachedEditor.GetDefinitionAsync(messageType, messageType.IndexOf("ItemRequest", StringComparison.Ordinal) + 2,
                TestContext.Current.CancellationToken);
            Assert.NotNull(definition);
            Assert.Equal(contract, definition.AssemblyPath);
            var decompiled = CSharpDecompilerService.Decompile(definition);
            Assert.Contains("class ItemRequest", decompiled.Text, StringComparison.Ordinal);
            Assert.Contains("Sku", decompiled.Text, StringComparison.Ordinal);
            const string partialScript = """
                var request = new Inventory.V1.ItemRequest { Sku = "extended" };
                Console.WriteLine(request.CustomValue);
                namespace Inventory.V1
                {
                    public partial class ItemRequest { public string CustomValue => Sku; }
                }
                """;
            var partialResult = await host.RunAsync(partialScript,
                (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data), TestContext.Current.CancellationToken);
            Assert.True(partialResult.Succeeded, string.Join('\n', errors));
            Assert.Equal("extended", output.Last());
            var partialPosition = partialScript.IndexOf("CustomValue", StringComparison.Ordinal) + 2;
            var partialDefinition = await cachedEditor.GetDefinitionAsync(partialScript, partialPosition, TestContext.Current.CancellationToken);
            Assert.True(partialDefinition?.IsInSource);
            // Returning to a normal script switches back to metadata, without duplicate definitions.
            Assert.Equal(contract, (await cachedEditor.GetDefinitionAsync(messageType,
                messageType.IndexOf("ItemRequest", StringComparison.Ordinal) + 2, TestContext.Current.CancellationToken))!.AssemblyPath);
            cachedEditor.UpdateExecutionContext(new ExecutionRequest
            {
                Platform = options.Platform,
                OutputDirectory = outputDir,
                Packages = packages,
            });
            Assert.Null(await cachedEditor.GetDefinitionAsync(messageType, messageType.IndexOf("ItemRequest", StringComparison.Ordinal) + 2,
                TestContext.Current.CancellationToken));

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
        var tab = new ScriptConnectionsViewModel(null, null, schemas.Drivers, schemas);
        tab.UsePackages(() => new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase), (_, _) => { });
        tab.Load(new RpadFrontMatter { Connections = [connection] });
        Assert.Equal("Its description couldn't be read: The server doesn't offer gRPC server reflection, which RunPad reads its services with.",
            Assert.Single(tab.Items).Problem);

        // A missing secret is such a reason too.
        var secretless = Inventory(metadata: "Bearer ${secret:nope}");
        await schemas.EnsureAsync([secretless], null, ConnectionSchemaRefresh.Missing, TestContext.Current.CancellationToken);
        Assert.Equal("The Secret Store has no secret named 'nope'. Add it in Tools → Secret Store.", schemas.Cache.ErrorFor(secretless));
    }

    // Like the Postman Echo server: reflection in the v1alpha protocol only, and a list request whose
    // list_services is empty is never answered.
    private sealed class PickyReflection : ServerReflection.ServerReflectionBase
    {
        public override async Task ServerReflectionInfo(IAsyncStreamReader<ServerReflectionRequest> requestStream,
            IServerStreamWriter<ServerReflectionResponse> responseStream, ServerCallContext context)
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                if (request.ListServices != "*") continue;
                var response = new ServerReflectionResponse { OriginalRequest = request, ListServicesResponse = new ListServiceResponse() };
                response.ListServicesResponse.Service.Add(new ServiceResponse { Name = "grpc.reflection.v1alpha.ServerReflection" });
                response.ListServicesResponse.Service.Add(new ServiceResponse { Name = "HelloService" });
                await responseStream.WriteAsync(response);
            }
        }
    }

    private static async Task<(WebApplication, string)> StartPickyAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        var app = builder.Build();
        app.MapGrpcService<PickyReflection>();
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    private static X509Certificate2 LoopbackCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static Dictionary<string, RpadValue> Params(string address, bool skipCertificate = false)
    {
        var parameters = new Dictionary<string, RpadValue>(StringComparer.Ordinal) { ["address"] = RpadValue.Of(address) };
        if (skipCertificate) parameters["skipCertificateCheck"] = RpadValue.Of("true");
        return parameters;
    }

    // A handler that sends every connection to a loopback port, whatever the address names: how
    // "http://grpc.postman-echo.com:443" reaches a local server without the network or port 443.
    private static Func<HttpMessageHandler> Redirect(string address) => () => new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, new Uri(address).Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    [Fact]
    public async Task Test_Should_UsePlaintextHttp2_OnAnyPort_AndAskForServicesTheWayServersAnswer()
    {
        var (server, address) = await StartPickyAsync();
        await using var _ = server;
        const string expected = "1 service: HelloService.";

        var regular = await new GrpcConnectionDriver().TestAsync(Params(address), TestContext.Current.CancellationToken);
        Assert.True(regular.Succeeded, regular.Message);
        Assert.Equal(expected, regular.Message);

        // The address of the Postman Echo server (manual check: Test on it with the same result).
        var on443 = await new GrpcConnectionDriver(Redirect(address)).TestAsync(Params("http://grpc.postman-echo.com:443"),
            TestContext.Current.CancellationToken);
        Assert.True(on443.Succeeded, on443.Message);
        Assert.Equal(expected, on443.Message);
    }

    [Fact]
    public async Task Test_Should_WorkOverHttps_AndKeepTheCertificateCheck()
    {
        using var certificate = LoopbackCertificate();
        var (server, address) = await StartAsync(reflection: true, certificate: certificate);
        await using var _ = server;
        Assert.StartsWith("https://", address, StringComparison.Ordinal);
        var driver = new GrpcConnectionDriver();

        var skipped = await driver.TestAsync(Params(address, skipCertificate: true), TestContext.Current.CancellationToken);
        Assert.True(skipped.Succeeded, skipped.Message);
        Assert.Equal("2 services: inventory.v1.Audit, inventory.v1.Stock.", skipped.Message);

        var checkedResult = await driver.TestAsync(Params(address), TestContext.Current.CancellationToken);
        Assert.False(checkedResult.Succeeded);
        Assert.StartsWith("Could not reach the server:", checkedResult.Message, StringComparison.Ordinal);
        Assert.EndsWith("The TLS handshake failed: if the server doesn't use TLS, use an http:// address.", checkedResult.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_Should_ExplainAProtocolMismatch()
    {
        using var certificate = LoopbackCertificate();
        var (http1, http1Address) = await StartAsync(reflection: true, HttpProtocols.Http1);
        var (tls, tlsAddress) = await StartAsync(reflection: true, certificate: certificate);
        await using var _ = http1;
        await using var __ = tls;
        var driver = new GrpcConnectionDriver();

        var http1Result = await driver.TestAsync(Params(http1Address), TestContext.Current.CancellationToken);
        var plainToTls = await driver.TestAsync(Params(tlsAddress.Replace("https://", "http://", StringComparison.Ordinal)), TestContext.Current.CancellationToken);
        var tlsToPlain = await driver.TestAsync(Params(http1Address.Replace("http://", "https://", StringComparison.Ordinal)), TestContext.Current.CancellationToken);
        const string noHttp2 = "The server doesn't speak HTTP/2, which gRPC needs: check the address and port, and use https:// if the server uses TLS.";
        Assert.False(http1Result.Succeeded);
        Assert.StartsWith("Could not reach the server:", http1Result.Message, StringComparison.Ordinal);
        Assert.EndsWith(noHttp2, http1Result.Message, StringComparison.Ordinal);

        Assert.False(plainToTls.Succeeded);
        Assert.StartsWith("Could not reach the server:", plainToTls.Message, StringComparison.Ordinal);
        // The HTTP/2 transport describes this mismatch differently across platforms. Both the
        // handshake-specific and the fallback hint direct the user to the HTTPS address.
        Assert.Contains("https://", plainToTls.Message, StringComparison.Ordinal);

        Assert.False(tlsToPlain.Succeeded);
        Assert.EndsWith("The TLS handshake failed: if the server doesn't use TLS, use an http:// address.", tlsToPlain.Message, StringComparison.Ordinal);
    }

    // The script's client reaches the server the way RunPad's own calls do: the address as written,
    // plaintext HTTP/2 for http:// (the Postman Echo one is on port 443), and the certificate check
    // left alone unless the connection asks to skip it.
    [Fact]
    public void TheClient_Should_UseTheAddressAsWritten_WithoutTlsOrCertificateChanges()
    {
        var schemas = Schemas();
        static string Source(ConnectionSchemas schemas, RpadConnection connection)
        {
            schemas.Cache.Put(connection, new ConnectionSchema("", [], "[]"));
            var generated = GeneratedConnections.For([connection], RpadLanguage.CSharp,
                new GrpcConnectionDriver().Packages.Keys, schemas.Drivers, schemas.Cache)!;
            Assert.Empty(generated.Errors);
            return generated.Source;
        }

        var plain = Source(schemas, Inventory("http://grpc.postman-echo.com:443", metadata: null));
        Assert.Contains("GrpcChannel.ForAddress(\"http://grpc.postman-echo.com:443\", new global::Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler });",
            plain, StringComparison.Ordinal);
        Assert.Contains("new global::System.Net.Http.SocketsHttpHandler { EnableMultipleHttp2Connections = true };", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteCertificateValidationCallback", plain, StringComparison.Ordinal);

        var connection = Inventory("https://localhost:5001", metadata: null);
        connection.Params["skipCertificateCheck"] = RpadValue.Of("true");
        Assert.Contains("RemoteCertificateValidationCallback = (_, _, _, _) => true;", Source(schemas, connection), StringComparison.Ordinal);
    }
}
