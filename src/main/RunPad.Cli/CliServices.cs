using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Connectors.Http;
using RunPad.Contracts;
using RunPad.Contracts.Connections;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Languages.VB;

namespace RunPad.Cli;

/// <summary>
/// What the runner runs scripts with: the registered languages, which build their hosts (with the
/// RunPad.Runtime library) from a run's request, the installed .NET SDKs, and the connection drivers a
/// script's <c>connections:</c> are built with. <see cref="CreateDefault"/> is the only place that knows
/// the concrete language modules, connectors and .NET backend; <see cref="CliRunner"/> uses just these contracts.
/// </summary>
public sealed record CliServices(ILanguageHostRegistry Registry, ISdkProvider Sdks, ConnectionDriverRegistry? ConnectionDrivers = null)
{
    public static CliServices CreateDefault()
    {
        var runtimeReferences = new DotnetRuntimeReferenceLocator();
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance, runtimeReferences));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance, runtimeReferences));
        var connectionDrivers = new ConnectionDriverRegistry();
        connectionDrivers.Register(new HttpConnectionDriver());
        return new CliServices(registry, new DotnetSdkProvider(), connectionDrivers);
    }
}
