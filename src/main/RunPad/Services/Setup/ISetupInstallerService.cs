namespace RunPad.Services.Setup;

public interface ISetupInstallerService
{
    /// <summary>Installs the .NET SDK via the official dotnet-install script. Returns the resolved dotnet executable path.</summary>
    Task<string> InstallDotnetSdkAsync(IProgress<string> progress, CancellationToken ct);

    /// <summary>Downloads and extracts netcoredbg from Samsung's GitHub releases. Returns the resolved netcoredbg executable path.</summary>
    Task<string> InstallNetcoredbgAsync(IProgress<string> progress, CancellationToken ct);
}
