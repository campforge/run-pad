using RunPad.Contracts.Models;

namespace RunPad.Contracts;

public interface ISdkProvider
{
    /// <summary>
    /// Lists the installed .NET SDKs. <paramref name="dotnetExecutableOverride"/> lets a caller that
    /// knows about a user-configured dotnet path (e.g. <c>DebuggerSettings.DotnetPath</c>) use it
    /// instead of resolving the bare <c>dotnet</c> command via PATH — <see langword="null"/>/empty
    /// falls back to PATH resolution, same as before this parameter existed.
    /// </summary>
    IReadOnlyList<DotnetSdk> Get(string? dotnetExecutableOverride = null);
}
