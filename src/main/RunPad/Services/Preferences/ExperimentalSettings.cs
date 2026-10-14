namespace RunPad.Services.Preferences;

/// <summary>Features that work but are not finished yet; all off by default
/// (Preferences → Experimental).</summary>
public sealed class ExperimentalSettings
{
    /// <summary>RunPad's own C# analyzers (RPAD… warnings) in the editor.</summary>
    public bool RunPadAnalyzers { get; set; }
}
