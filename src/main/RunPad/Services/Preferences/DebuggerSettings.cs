namespace RunPad.Services.Preferences;

public sealed class DebuggerSettings
{
    // "dotnet" (no absolute path) resolves via PATH on every OS — same as how DotnetSdkProvider
    // itself finds the SDK (Utils.Shell("dotnet --list-sdks")). A hardcoded Windows Program Files
    // path here was wrong on Linux/macOS by construction; users who need a specific install can
    // still override this in Preferences.
    public string DotnetPath { get; set; } = "dotnet";
    public string DebuggerPath { get; set; } = "";
}
