using Invoklet.Connectors.Grpc;
using Invoklet.Connectors.Http;
using Invoklet.Connectors.Sqlite;
using Invoklet.Contracts;
using Invoklet.Contracts.Connections;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Sdk;
using Invoklet.Languages.VB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invoklet.Cli;

/// <summary>
/// What the runner runs scripts with: the registered languages, which build their hosts (with the
/// Invoklet.Runtime library) from a run's request, the installed .NET SDKs, and the connection drivers a
/// script's <c>connections:</c> are built with, and what some of them read from their endpoint first
/// (a gRPC server's services), shared with the Invoklet window. <see cref="CreateDefault"/> is the only place that knows
/// the concrete language modules, connectors and .NET backend; <see cref="CliRunner"/> uses just these contracts.
/// </summary>
public sealed record CliServices(ILanguageHostRegistry Registry, ISdkProvider Sdks, ConnectionDriverRegistry? ConnectionDrivers = null,
    ConnectionSchemas? ConnectionSchemas = null)
{
    public static CliServices CreateDefault()
    {
        var runtimeReferences = new DotnetRuntimeReferenceLocator();
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance, runtimeReferences));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance, runtimeReferences));
        var connectionDrivers = new ConnectionDriverRegistry();
        connectionDrivers.Register(new HttpConnectionDriver());
        connectionDrivers.Register(new GrpcConnectionDriver());
        connectionDrivers.Register(new SqliteConnectionDriver());
        var schemas = new ConnectionSchemas(connectionDrivers, ConnectionSchemaCache.DefaultFolder,
            DotnetConnectionTools.Default, Secret);
        return new CliServices(registry, new DotnetSdkProvider(), connectionDrivers, schemas);
    }

    // The Secret Store's value, read as a script reads it; a missing or unreadable one is null, which
    // the schema reports as missing.
    private static string? Secret(string name)
    {
        try { return Util.GetSecret(name); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
