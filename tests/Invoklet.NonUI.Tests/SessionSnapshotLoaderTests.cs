using Invoklet.Format.RPad;
using Invoklet.Services.Recovery;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class SessionSnapshotLoaderTests
{
    [Fact]
    public async Task CleanNamedTab_IsRevalidatedFromDisk()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, RpadWriter.NewFile("1 + 1", "Demo"));
            var tab = new RecoveryTab { FilePath = path, EditorText = "stale", IsDirty = false };
            var result = (await new DiskSessionSnapshotLoader().LoadAsync([tab]))[0];
            Assert.Equal("1 + 1\n", result.Document!.RunCode);
            Assert.True(result.BaselineKnown);
            Assert.NotNull(result.DiskHash);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DirtyTab_IsNotReadFromDisk()
    {
        var tab = new RecoveryTab { FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), EditorText = "draft", IsDirty = true };
        var result = (await new DiskSessionSnapshotLoader().LoadAsync([tab]))[0];
        Assert.Equal("draft", result.EditorText);
        Assert.True(result.IsDirty);
    }

    [Fact]
    public async Task MissingFile_BecomesDirtyAndKeepsDraft()
    {
        var tab = new RecoveryTab { FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), EditorText = "draft" };
        var result = (await new DiskSessionSnapshotLoader().LoadAsync([tab]))[0];
        Assert.Equal("draft", result.EditorText);
        Assert.True(result.IsDirty);
    }

    [Fact]
    public async Task ParseError_PreservesTextAndReportsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "// <Invoklet Kind=\"Statements\" Language=\"csharp\">\n{ invalid");
            var tab = new RecoveryTab { FilePath = path, EditorText = "stale" };
            var result = (await new DiskSessionSnapshotLoader().LoadAsync([tab]))[0];
            Assert.NotNull(result.ParseError);
            Assert.True(result.BaselineKnown);
        }
        finally { File.Delete(path); }
    }
}
