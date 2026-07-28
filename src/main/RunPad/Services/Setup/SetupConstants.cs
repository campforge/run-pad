namespace RunPad.Services.Setup;

// Every external source SetupInstallerService pulls from, plus install-time limits — kept in one
// place so an update (a new netcoredbg pin, a changed dotnet-install redirect) doesn't require
// hunting through the orchestration logic for a buried literal.
internal static class SetupConstants
{
    public const string DotnetInstallScriptUrlWindows = "https://dot.net/v1/dotnet-install.ps1";
    public const string DotnetInstallScriptUrlUnix = "https://dot.net/v1/dotnet-install.sh";

    public const string NetcoredbgLatestBase = "https://github.com/Samsung/netcoredbg/releases/latest/download";
    public const string NetcoredbgReleasesBase = "https://github.com/Samsung/netcoredbg/releases/download";

    // netcoredbg's newest release (3.2.0) dropped the osx-amd64 (Intel Mac) build that every release
    // back to 3.1.0 shipped — pin the last release known to have one for that specific platform/arch
    // combo only; every other platform/arch tracks the real "latest" release.
    public const string NetcoredbgIntelMacPinnedTag = "3.1.3-1062";

    // Bounds how long a single install step (download + run/extract) can run before it's treated as
    // failed — without this, a stalled download or a wedged child process left the wizard stuck on
    // IsBusy forever with no error ever surfacing (SetupWizardViewModel had no way to observe that
    // nothing was happening, since CancellationToken.None was passed all the way through).
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
}
