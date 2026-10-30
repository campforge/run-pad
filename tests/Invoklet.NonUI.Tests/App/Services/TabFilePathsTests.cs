using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

// TabFilePaths is a static function of paths; there is nothing to construct or to mock.
public sealed class TabFilePathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "tabs-root");

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void DecideSaveAs_PickerCancelled_IsCancelled()
    {
        // Arrange (nothing chosen)

        // Act
        var outcome = TabFilePaths.DecideSaveAs(null, P("a.inv"), null, false, StringComparison.Ordinal);

        // Assert
        Assert.Equal(SaveAsOutcome.Cancelled, outcome);
    }

    [Fact]
    public void DecideSaveAs_AnotherFile_IsWritten()
    {
        // Arrange (an untitled tab saved to a new file)

        // Act
        var outcome = TabFilePaths.DecideSaveAs(P("new.inv"), null, null, false, StringComparison.Ordinal);

        // Assert
        Assert.Equal(SaveAsOutcome.Write, outcome);
    }

    [Fact]
    public void DecideSaveAs_TheTabsOwnFileAgain_IsAnInPlaceSave()
    {
        // Arrange (a different spelling of the same path)
        var spelled = P("sub", "..", "a.inv");

        // Act
        var outcome = TabFilePaths.DecideSaveAs(spelled, P("a.inv"), null, false, StringComparison.Ordinal);

        // Assert
        Assert.Equal(SaveAsOutcome.InPlace, outcome);
    }

    [Fact]
    public void DecideSaveAs_AFileAnotherTabHasOpen_IsRejected()
    {
        // Arrange (nothing else matters when another tab has the file)

        // Act
        var outcome = TabFilePaths.DecideSaveAs(P("b.inv"), P("a.inv"), null, true, StringComparison.Ordinal);

        // Assert
        Assert.Equal(SaveAsOutcome.Rejected, outcome);
    }

    [Fact]
    public void DecideSaveAs_ARecoveredDraftOntoItsOriginal_IsRejected()
    {
        // Arrange (an untitled copy of a file that is open in another tab)

        // Act
        var outcome = TabFilePaths.DecideSaveAs(P("a.inv"), null, P("a.inv"), false, StringComparison.Ordinal);

        // Assert
        Assert.Equal(SaveAsOutcome.Rejected, outcome);
    }

    [Fact]
    public void DecideSaveAs_ARecoveredDraftOntoAnotherFile_IsWritten()
    {
        // Arrange

        // Act
        var outcome = TabFilePaths.DecideSaveAs(P("copy.inv"), null, P("a.inv"), false, StringComparison.Ordinal);

        // Assert
        Assert.Equal(SaveAsOutcome.Write, outcome);
    }

    [Fact]
    public void DecideSaveAs_OriginalOfADraftThatHasAFileNow_DoesNotRejectItsOwnFile()
    {
        // Arrange (the tab was saved, so it has a file of its own; the original no longer guards anything)

        // Act
        var outcome = TabFilePaths.DecideSaveAs(P("a.inv"), P("a.inv"), P("a.inv"), false, StringComparison.Ordinal);

        // Assert
        Assert.Equal(SaveAsOutcome.InPlace, outcome);
    }

    [Fact]
    public void DecideSaveAs_CaseInsensitiveComparison_SeesTheSameFile()
    {
        // Arrange

        // Act
        var outcome = TabFilePaths.DecideSaveAs(P("A.INV"), P("a.inv"), null, false, StringComparison.OrdinalIgnoreCase);

        // Assert
        Assert.Equal(SaveAsOutcome.InPlace, outcome);
    }

    [Fact]
    public void NextCopyPath_NoCopyYet_IsNameCopy()
    {
        // Arrange (nothing exists)

        // Act
        var path = TabFilePaths.NextCopyPath(P("query.inv"), _ => false);

        // Assert
        Assert.Equal(P("query copy.inv"), path);
    }

    [Fact]
    public void NextCopyPath_CopiesExist_IsTheFirstFreeNumber()
    {
        // Arrange
        HashSet<string> taken = [P("query copy.inv"), P("query copy 2.inv")];

        // Act
        var path = TabFilePaths.NextCopyPath(P("query.inv"), taken.Contains);

        // Assert
        Assert.Equal(P("query copy 3.inv"), path);
    }

    [Fact]
    public void NextCopyPath_FileWithoutExtension_KeepsItThatWay()
    {
        // Arrange (nothing exists)

        // Act
        var path = TabFilePaths.NextCopyPath(P("notes"), _ => false);

        // Assert
        Assert.Equal(P("notes copy"), path);
    }
}
