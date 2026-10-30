namespace Invoklet.Services.Preferences;

/// <summary>Features that work but are not finished yet; all off by default
/// (Preferences → Experimental).</summary>
public sealed class ExperimentalSettings
{
    /// <summary>Invoklet's own C# analyzers (RPAD… warnings) in the editor.</summary>
    public bool InvokletAnalyzers { get; set; }
}
