namespace Invoklet.Services.Tabs;

/// <summary>What the lifecycle manager needs from the view of a tab.</summary>
internal interface ITabView
{
    /// <summary>A script of the tab is running or being debugged.</summary>
    bool HasRunningProcess { get; }

    /// <summary>The text and the output are on disk, not in the view.</summary>
    bool IsContentReleased { get; }

    /// <summary>Moves the text and the output to disk. False: nothing was released.</summary>
    bool TryReleaseContent();

    /// <summary>Brings them back. False: they could not be read, and the tab stays released.</summary>
    bool TryRestoreContent();

    /// <summary>Only the view being looked at colours its text.</summary>
    void SetHighlightingActive(bool active);
}
