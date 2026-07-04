using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace DotPad.Languages.CSharp.IntelliSense;

internal static class DotPadRoslynHost
{
    private static readonly Lazy<HostServices> _hostServices =
        new(CreateHostServices, LazyThreadSafetyMode.ExecutionAndPublication);

    public static HostServices HostServices => _hostServices.Value;

    private static HostServices CreateHostServices()
    {
        // typeof guarantees the assembly is already loaded via JIT — no Assembly.Load needed
        var wsCommon = typeof(AdhocWorkspace).Assembly;
        var csharpWs = LoadCSharpWorkspaces(wsCommon);

        return MefHostServices.Create(
            csharpWs != null ? [wsCommon, csharpWs] : [wsCommon]);
    }

    private static Assembly? LoadCSharpWorkspaces(Assembly wsCommon)
    {
        const string name = "Microsoft.CodeAnalysis.CSharp.Workspaces";

        // Fast path: already in AppDomain
        var existing = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        if (existing != null) return existing;

        var loc = wsCommon.Location;
        if (!string.IsNullOrEmpty(loc))
        {
            // Self-contained / published: all DLLs in the same directory
            var sameDir = Path.Combine(Path.GetDirectoryName(loc)!, name + ".dll");
            if (File.Exists(sameDir)) return Assembly.LoadFrom(sameDir);

            // NuGet cache: parallel package directory
            //   .../microsoft.codeanalysis.workspaces.common/4.x/lib/tfm/X.dll
            // → .../microsoft.codeanalysis.csharp.workspaces/4.x/lib/tfm/Y.dll
            var nuget = loc.Replace(
                "microsoft.codeanalysis.workspaces.common",
                "microsoft.codeanalysis.csharp.workspaces",
                StringComparison.OrdinalIgnoreCase);
            nuget = Path.Combine(Path.GetDirectoryName(nuget)!, name + ".dll");
            if (File.Exists(nuget)) return Assembly.LoadFrom(nuget);
        }

        // Last resort: let the runtime resolve via deps.json
        try { return Assembly.Load(name); }
        catch { return null; }
    }
}
