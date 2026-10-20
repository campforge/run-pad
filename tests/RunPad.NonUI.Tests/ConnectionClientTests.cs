using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Build.Core.Models;
using RunPad.Connectors.Http;
using RunPad.Contracts.Connections;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Format.RPad;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Runtime.UI.Tests;
using RunPad.Services.Secrets;
using Xunit;

namespace RunPad.NonUI.Tests;

// A script's connections become a Connections class with a client per connection: compiled with the
// script, and known to the editor before any run. RUNPAD_SECRETS_FILE is set here, so these share
// the real-build collection with the other tests that set it.
[Collection(RealBuildTestCollection.Name)]
public sealed class ConnectionClientTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("runpad-connection-clients").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private static ConnectionDriverRegistry Drivers()
    {
        var registry = new ConnectionDriverRegistry();
        registry.Register(new HttpConnectionDriver());
        return registry;
    }

    private static RpadConnection Billing(string address = "https://billing.example/api/") => new()
    {
        Name = "billing-api",
        Driver = "http",
        Params = new(StringComparer.Ordinal)
        {
            ["baseAddress"] = RpadValue.Of(address),
            ["headers"] = RpadValue.Map(
            [
                new("Authorization", RpadValue.Of("Bearer ${secret:billing-token}")),
                new("X-Tenant", RpadValue.Of("${env:RUNPAD_TEST_TENANT}-\"q\"")),
            ]),
            ["timeout"] = RpadValue.Of("12.5"),
        },
    };

    [Fact]
    public void TheGeneratedCode_Should_ReadSecretsAndVariablesOnlyWhenRun()
    {
        var generated = GeneratedConnections.For([Billing()], RpadLanguage.CSharp, [], Drivers())!;
        Assert.Empty(generated.Errors);
        Assert.Contains("public static BillingApiClient BillingApi => __billingApi ??= new BillingApiClient();", generated.Source);
        Assert.Contains("public sealed class BillingApiClient : global::System.Net.Http.HttpClient", generated.Source);
        Assert.Contains("\"Bearer \" + global::Util.GetSecret(\"billing-token\")", generated.Source);
        Assert.Contains("global::System.Environment.GetEnvironmentVariable(\"RUNPAD_TEST_TENANT\")", generated.Source);
        Assert.Contains("\"-\\\"q\\\"\"", generated.Source);
        Assert.DoesNotContain("${", generated.Source);
    }

    [Fact]
    public void ProblemsWithConnections_Should_BeBuildErrors_ThatKeepTheValidOnes()
    {
        var missingAddress = new RpadConnection { Name = "broken", Driver = "http" };
        var unknown = new RpadConnection { Name = "warehouse", Driver = "snowflake" };
        var generated = GeneratedConnections.For([Billing(), missingAddress, unknown], RpadLanguage.CSharp, [], Drivers())!;
        Assert.Equal(
            ["Connection 'broken': The base address is missing.", "Connection 'warehouse': this RunPad has no 'snowflake' driver."],
            generated.Errors);
        Assert.Contains("BillingApi", generated.Source);
        Assert.DoesNotContain("Broken", generated.Source);

        Assert.Null(GeneratedConnections.For([], RpadLanguage.CSharp, [], Drivers()));
        Assert.Equal(["Connections can be used in C# scripts only, for now."],
            GeneratedConnections.For([Billing()], RpadLanguage.Vb, [], Drivers())!.Errors);
    }

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
        public ConnectionClientCode CreateClient(RpadConnection connection, ConnectionCodeContext context) =>
            new($"public sealed class {context.ClassName} {{ internal {context.ClassName}() {{ }} }}");
    }

    // A driver's packages are the script's own: without them in packages: the build fails, saying which.
    [Fact]
    public void ADriversPackage_MissingFromTheScript_Should_BeABuildError()
    {
        var drivers = Drivers();
        drivers.Register(new SqlLikeDriver());
        var local = new RpadConnection { Name = "local", Driver = "sqlish" };
        Assert.Equal(["Connection 'local' needs the NuGet package Microsoft.Data.Sqlite: add it in Dependencies → Packages."],
            GeneratedConnections.For([local], RpadLanguage.CSharp, [], drivers)!.Errors);
        Assert.Empty(GeneratedConnections.For([local], RpadLanguage.CSharp, ["microsoft.data.sqlite"], drivers)!.Errors);
    }

    // The editor knows Connections.BillingApi, an HttpClient, as soon as the script lists it.
    [Fact]
    public async Task TheEditor_Should_CompleteTheClient_BeforeAnyRun()
    {
        var referenceService = new CSharpReferenceService();
        using var service = new CSharpIntelliSenseService(
            referenceService.GetDefault().Append(referenceService.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance);
        service.UpdateExecutionContext(new ExecutionRequest
        {
            Connections = GeneratedConnections.For([Billing()], RpadLanguage.CSharp, [], Drivers()),
        });

        const string code = "var text = await Connections.BillingApi.GetStr";
        var members = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);
        Assert.Contains(members!.Items, i => i.DisplayText == "GetStringAsync");
        const string root = "Connections.";
        var connections = await service.GetCompletionsAsync(root, root.Length, ct: TestContext.Current.CancellationToken);
        Assert.Contains(connections!.Items, i => i.DisplayText == "BillingApi");

        // Removing the connection removes the class again.
        service.UpdateExecutionContext(new ExecutionRequest());
        var none = await service.GetCompletionsAsync(root, root.Length, ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(none?.Items ?? [], i => i.DisplayText == "BillingApi");
    }

    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task AScript_Should_UseItsConnection_WithTheSecretFilledInWhenItRuns()
    {
        if (!new DotnetSdkProvider().Get().Any()) return;
        var secretsFile = Path.Combine(_folder, "secrets.json");
        new FileSecretStore(secretsFile).Save([new Secret("billing-token", "t0k")]);
        var previousSecrets = Environment.GetEnvironmentVariable(FileSecretStore.FileEnvVar);
        Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, secretsFile);
        Environment.SetEnvironmentVariable("RUNPAD_TEST_TENANT", "acme");
        try
        {
            var outputDir = Path.Combine(_folder, "build");
            Directory.CreateDirectory(outputDir);
            var options = CompiledDotnetLanguageHostOptions.From(new ExecutionRequest
            {
                Platform = new DotnetSdkProvider().Get().First(),
                OutputDirectory = outputDir,
                Connections = GeneratedConnections.For([Billing()], RpadLanguage.CSharp, [], Drivers()),
            }, new CSharpReferenceService().GetExtensionReference());
            await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
            var output = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            const string script = """
                var api = Connections.BillingApi;
                Console.WriteLine(api.BaseAddress);
                Console.WriteLine(api.DefaultRequestHeaders.Authorization);
                Console.WriteLine(string.Join(",", api.DefaultRequestHeaders.GetValues("X-Tenant")));
                Console.WriteLine(api.Timeout.TotalSeconds);
                Console.WriteLine(ReferenceEquals(api, Connections.BillingApi));
                """;
            var result = await host.RunAsync(script, (data, kind) => (kind == OutputKind.StandardError ? errors : output).Enqueue(data),
                    TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, string.Join('\n', errors));
            Assert.Equal(["https://billing.example/api/", "Bearer t0k", "acme-\"q\"", "12.5", "True"], output.TakeLast(5));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileSecretStore.FileEnvVar, previousSecrets);
            Environment.SetEnvironmentVariable("RUNPAD_TEST_TENANT", null);
        }
    }

    [Fact]
    [Trait("Category", "RealBuild")]
    public async Task InvalidConnections_Should_FailTheBuild_WithTheirReason()
    {
        if (!new DotnetSdkProvider().Get().Any()) return;
        var outputDir = Path.Combine(_folder, "build-invalid");
        Directory.CreateDirectory(outputDir);
        var options = CompiledDotnetLanguageHostOptions.From(new ExecutionRequest
        {
            Platform = new DotnetSdkProvider().Get().First(),
            OutputDirectory = outputDir,
            Connections = GeneratedConnections.For([new RpadConnection { Name = "broken", Driver = "http" }], RpadLanguage.CSharp, [], Drivers()),
        }, new CSharpReferenceService().GetExtensionReference());
        await using var host = new CSharpLanguageHost(options, NullLoggerFactory.Instance);
        var errors = new ConcurrentQueue<string>();
        var result = await host.RunAsync("Console.WriteLine(1);", (data, kind) => { if (kind == OutputKind.StandardError) errors.Enqueue(data); },
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(RunOutcome.CompilationFailed, result.Outcome);
        Assert.Contains(errors, e => e.Contains("Connection 'broken': The base address is missing.", StringComparison.Ordinal));
    }
}
