using System.ComponentModel;
using RunPad.Contracts;
using RunPad.Services.Preferences;

namespace RunPad.Services.Setup;

public sealed class PrerequisiteService(ISdkProvider sdkProvider, IPreferencesService preferences) : IPrerequisiteService
{
    public PrerequisiteStatus Check()
    {
        // DotnetSdkProvider shells out to `dotnet --list-sdks` (or the configured DotnetPath override,
        // if the user picked one manually in the setup wizard/Preferences) — if that executable isn't
        // found at all (the exact case this service exists to detect), Process.Start throws
        // Win32Exception rather than returning an empty list.
        bool hasSuitableSdk;
        try { hasSuitableSdk = sdkProvider.Get(preferences.Debugger.DotnetPath).Count > 0; }
        catch (Win32Exception) { hasSuitableSdk = false; }

        var debuggerPath = preferences.Debugger.DebuggerPath;
        var hasValidDebugger = !string.IsNullOrWhiteSpace(debuggerPath) && File.Exists(debuggerPath);

        return new PrerequisiteStatus(hasSuitableSdk, hasValidDebugger);
    }
}
