namespace DotPad.Services.Setup;

// Thin seam around Process so SetupInstallerService's OS/arch/URL orchestration logic can be unit
// tested with a fake — no real dotnet-install/netcoredbg process spawned in tests.
public interface ISetupProcessRunner
{
    Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, IProgress<string>? progress, CancellationToken ct);
}
