using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts;
using RunPad.Languages.CSharp;
using RunPad.Languages.DotnetCommon;
using RunPad.Languages.DotnetCommon.Sdk;
using RunPad.Languages.VB;

namespace RunPad.Cli;

/// <summary>
/// What the runner runs scripts with: the registered languages, the installed .NET SDKs and the
/// RunPad.Runtime library. <see cref="CreateDefault"/> is the only place that knows the concrete
/// language modules and .NET backend; <see cref="CliRunner"/> uses just these contracts.
/// </summary>
public sealed record CliServices(
    ILanguageHostRegistry Registry, ISdkProvider Sdks, IRuntimeReferenceLocator RuntimeReferences)
{
    public static CliServices CreateDefault()
    {
        var registry = new LanguageHostRegistry();
        registry.Register(new CSharpLanguageHostFactory(NullLoggerFactory.Instance));
        registry.Register(new VbLanguageHostFactory(NullLoggerFactory.Instance));
        return new CliServices(registry, new DotnetSdkProvider(), new DotnetRuntimeReferenceLocator());
    }
}
