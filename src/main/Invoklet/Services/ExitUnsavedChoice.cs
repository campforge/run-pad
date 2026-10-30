namespace Invoklet.Services;

/// <summary>What to do with unsaved tabs when the app is closed while draft recovery is on.</summary>
public enum ExitUnsavedChoice
{
    /// <summary>Keep the app open; also closing the dialog or having no window to show it on.</summary>
    Cancel,

    /// <summary>Save every unsaved tab (Save As for untitled ones) before closing.</summary>
    SaveAll,

    /// <summary>Close, keeping the unsaved tabs as drafts for the next start; files are not written.</summary>
    KeepInStash,
}
