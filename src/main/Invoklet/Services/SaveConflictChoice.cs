namespace Invoklet.Services;

/// <summary>
/// The user's choice when a script changed on disk since Invoklet last read or wrote it. Cancel is
/// the zero value: whatever doesn't make a choice (closing the dialog, no window to show it on)
/// leaves the file alone.
/// </summary>
public enum SaveConflictChoice
{
    Cancel,
    SaveAs,
    Overwrite,
}
