namespace DotPad.Services;

/// <summary>User's choice from the "this tab has unsaved changes" confirmation dialog.</summary>
public enum UnsavedChangesResult
{
    Save,
    DontSave,
    Cancel,
}
