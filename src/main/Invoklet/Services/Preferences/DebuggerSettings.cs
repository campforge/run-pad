namespace Invoklet.Services.Preferences;

public sealed class DebuggerSettings
{
    // "dotnet" (no absolute path) resolves via PATH on every OS. A hardcoded Windows Program Files
    // path here was wrong on Linux/macOS by construction; users who need a specific install can
    // override this in Preferences or the first-run setup wizard (DotnetSdkProvider.Get() takes it
    // as an override, falling back to bare "dotnet" when it's still this literal default).
    public string DotnetPath { get; set; } = "dotnet";
    public string DebuggerPath { get; set; } = "";
}
