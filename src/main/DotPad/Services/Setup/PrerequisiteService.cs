using System.ComponentModel;
using DotPad.Contracts;
using DotPad.Services.Preferences;

namespace DotPad.Services.Setup;

public sealed class PrerequisiteService(ISdkProvider sdkProvider, IPreferencesService preferences) : IPrerequisiteService
{
    public PrerequisiteStatus Check()
    {
        // DotnetSdkProvider shells out to `dotnet --list-sdks` — if `dotnet` isn't on PATH at all
        // (the exact case this service exists to detect), Process.Start throws Win32Exception rather
        // than returning an empty list.
        bool hasSuitableSdk;
        try { hasSuitableSdk = sdkProvider.Get().Count > 0; }
        catch (Win32Exception) { hasSuitableSdk = false; }

        var debuggerPath = preferences.Debugger.DebuggerPath;
        var hasValidDebugger = !string.IsNullOrWhiteSpace(debuggerPath) && File.Exists(debuggerPath);

        return new PrerequisiteStatus(hasSuitableSdk, hasValidDebugger);
    }
}
