using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Languages.VB;

namespace RunPad.Cli;

/// <summary>
/// What the runner runs scripts with: the registered languages, which build their hosts (with the
/// RunPad.Runtime library) from a run's request, and the installed .NET SDKs.
/// <see cref="CreateDefault"/> is the only place that knows the concrete language modules and .NET
/// backend; <see cref="CliRunner"/> uses just these contracts.
/// </summary>
public sealed record CliServices(ILanguageHostRegistry Registry, ISdkProvider Sdks)
{
    public static CliServices CreateDefault()
    {
        var runtimeReferences = new DotnetRuntimeReferenceLocator();
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance, runtimeReferences));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance, runtimeReferences));
        return new CliServices(registry, new DotnetSdkProvider());
    }
}
