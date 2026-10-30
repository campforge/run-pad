using System.Composition.Hosting;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Invoklet.Languages.CSharp.IntelliSense.Roslyn;

internal static class InvokletRoslynHost
{
    // Roslyn's own MEF export lookup (IMefHostExportProvider.GetExports<T, TMetadata>) is internal
    // to Microsoft.CodeAnalysis.Features, so RoslynCodeFixEngine needs its own handle on the same
    // composition to query CodeFixProvider exports directly via the public System.Composition API
    // — hence building (and exposing) the CompositionHost ourselves instead of going through
    // MefHostServices.Create(IEnumerable<Assembly>), then handing that same context to
    // MefHostServices.Create(CompositionContext) so the AdhocWorkspace gets the identical set of
    // parts (single composition, not two independent ones).
    private static readonly Lazy<CompositionHost> _compositionHost =
        new(CreateCompositionHost, LazyThreadSafetyMode.ExecutionAndPublication);

    public static CompositionHost CompositionHost => _compositionHost.Value;

    public static HostServices HostServices => MefHostServices.Create(CompositionHost);

    // RoslynAnalyzerEngine's DiagnosticAnalyzer
    // discovery can't go through CompositionHost/MEF the way CodeFixProvider/CodeRefactoringProvider do:
    // confirmed via a live probe (compositionHost.GetExports<DiagnosticAnalyzer>() returns 0) that
    // analyzers were never [Export]-decorated for this composition — VS discovers them a completely
    // different way (AnalyzerReference on the project, not MEF). Plain reflection over the assembly's
    // own types instead, hence needing a direct handle on it (LoadSiblingAssembly does the same
    // self-contained/NuGet-cache/deps.json resolution CreateCompositionHost already relies on — the
    // AppDomain fast path means this doesn't redo any real work once CompositionHost has been built).
    private static readonly Lazy<Assembly> _csharpFeaturesAssembly =
        new(() => LoadSiblingAssembly(typeof(AdhocWorkspace).Assembly, "Microsoft.CodeAnalysis.CSharp.Features")
            ?? throw new InvalidOperationException("Microsoft.CodeAnalysis.CSharp.Features could not be located."),
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static Assembly CSharpFeaturesAssembly => _csharpFeaturesAssembly.Value;

    private static CompositionHost CreateCompositionHost()
    {
        // typeof guarantees the assembly is already loaded via JIT — no Assembly.Load needed
        var wsCommon = typeof(AdhocWorkspace).Assembly;

        // Microsoft.CodeAnalysis.Features/CSharp.Features register their CodeFixProviders (Add
        // Import, Implement Interface, Generate Variable, ...) as MEF parts — pulling their
        // assemblies into the composition is what makes RoslynCodeFixEngine (the lightbulb
        // engine) able to discover them.
        var assemblies = new List<Assembly> { wsCommon };
        TryLoad(wsCommon, "Microsoft.CodeAnalysis.CSharp.Workspaces", assemblies);
        TryLoad(wsCommon, "Microsoft.CodeAnalysis.Features", assemblies);
        TryLoad(wsCommon, "Microsoft.CodeAnalysis.CSharp.Features", assemblies);

        return new ContainerConfiguration().WithAssemblies(assemblies).CreateContainer();
    }

    private static void TryLoad(Assembly anchor, string name, List<Assembly> into)
    {
        var asm = LoadSiblingAssembly(anchor, name);
        if (asm != null) into.Add(asm);
    }

    // Loads an assembly that ships alongside `anchor` (same directory when self-contained/published,
    // or the parallel NuGet package cache directory during `dotnet run`).
    private static Assembly? LoadSiblingAssembly(Assembly anchor, string name)
    {
        // Fast path: already in AppDomain
        var existing = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        if (existing != null) return existing;

        var loc = anchor.Location;
        if (!string.IsNullOrEmpty(loc))
        {
            // Self-contained / published: all DLLs in the same directory
            var sameDir = Path.Combine(Path.GetDirectoryName(loc)!, name + ".dll");
            if (File.Exists(sameDir)) return Assembly.LoadFrom(sameDir);

            // NuGet cache: parallel package directory
            //   .../microsoft.codeanalysis.workspaces.common/4.x/lib/tfm/X.dll
            // → .../<name, lowercased>/4.x/lib/tfm/Y.dll
            var nuget = loc.Replace(
                "microsoft.codeanalysis.workspaces.common",
                name.ToLowerInvariant(),
                StringComparison.OrdinalIgnoreCase);
            nuget = Path.Combine(Path.GetDirectoryName(nuget)!, name + ".dll");
            if (File.Exists(nuget)) return Assembly.LoadFrom(nuget);
        }

        // Last resort: let the runtime resolve via deps.json
        try { return Assembly.Load(name); }
        catch { return null; }
    }
}
