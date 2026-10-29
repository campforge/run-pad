using Xunit;

namespace Invoklet.Runtime.Tests;

// LINQPad-compatible Util.SaveString/LoadString/SaveBytes/LoadBytes/Try.
// Not [Collection(ConsoleTestCollection.Name)] like UtilTests — none of this touches Console.Out/In,
// so there's no reason to serialize it against those tests. SaveString/SaveBytes write into the
// REAL %AppData%/Invoklet/UserData folder (no injectable path seam exists for a static-class API like
// this — same tradeoff BuildCacheService's own tests accepted before it grew one)
// — every test below uses its own Guid-named file so
// concurrent test runs can't collide, and cleans up after itself.
public class UtilFileAndTryTests
{
    [Fact]
    public void SaveString_Should_WriteAFile_ThatLoadStringReadsBack()
    {
        var fileName = $"Invoklet-test-{Guid.NewGuid()}.txt";
        try
        {
            Util.SaveString("hello world", fileName);

            Assert.Equal("hello world", Util.LoadString(fileName));
        }
        finally { DeleteIfExists(fileName); }
    }

    [Fact]
    public void SaveString_Should_OverwriteAnExistingFile()
    {
        var fileName = $"Invoklet-test-{Guid.NewGuid()}.txt";
        try
        {
            Util.SaveString("v1", fileName);
            Util.SaveString("v2", fileName);

            Assert.Equal("v2", Util.LoadString(fileName));
        }
        finally { DeleteIfExists(fileName); }
    }

    [Fact]
    public void LoadString_Should_Throw_WhenTheFileDoesNotExist()
    {
        var fileName = $"Invoklet-test-{Guid.NewGuid()}-missing.txt";

        Assert.ThrowsAny<IOException>(() => Util.LoadString(fileName));
    }

    [Fact]
    public void SaveBytes_Should_WriteAFile_ThatLoadBytesReadsBack()
    {
        var fileName = $"Invoklet-test-{Guid.NewGuid()}.bin";
        byte[] data = [1, 2, 3, 4, 5];
        try
        {
            Util.SaveBytes(data, fileName);

            Assert.Equal(data, Util.LoadBytes(fileName));
        }
        finally { DeleteIfExists(fileName); }
    }

    [Fact]
    public void LoadBytes_Should_Throw_WhenTheFileDoesNotExist()
    {
        var fileName = $"Invoklet-test-{Guid.NewGuid()}-missing.bin";

        Assert.ThrowsAny<IOException>(() => Util.LoadBytes(fileName));
    }

    [Fact]
    public void SaveString_Should_CreateSubdirectories_WhenFileNameIncludesThem()
    {
        var fileName = Path.Combine($"Invoklet-test-{Guid.NewGuid()}", "nested.txt");
        try
        {
            Util.SaveString("nested content", fileName);

            Assert.Equal("nested content", Util.LoadString(fileName));
        }
        finally { DeleteIfExists(fileName); }
    }

    private static void DeleteIfExists(string fileName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "UserData");
        var path = Path.Combine(root, fileName);
        if (File.Exists(path)) File.Delete(path);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && dir != root && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // A block-bodied lambda ("() => { ran = true; }"), not an expression-bodied one ("() => ran =
    // true") — the latter is itself a valid bool-valued expression, so the compiler prefers the
    // Try<T>(Func<T>) overload over Try(Action) for it. A real, easy-to-hit trap for anyone calling
    // Util.Try with a single-assignment lambda — worth a comment here since it silently compiles to
    // the "wrong" overload rather than failing to compile.
    [Fact]
    public void Try_Should_ReturnNull_WhenTheActionCompletesWithoutThrowing()
    {
        var ran = false;

        var result = Util.Try(() => { ran = true; });

        Assert.Null(result);
        Assert.True(ran);
    }

    [Fact]
    public void Try_Should_ReturnTheException_WhenTheActionThrows()
    {
        var thrown = new InvalidOperationException("boom");

        var result = Util.Try(() => throw thrown);

        Assert.Same(thrown, result);
    }

    [Fact]
    public void TryOfT_Should_ReturnTheResult_AndNullException_WhenTheFunctionSucceeds()
    {
        var (result, exception) = Util.Try(() => 42);

        Assert.Equal(42, result);
        Assert.Null(exception);
    }

    [Fact]
    public void TryOfT_Should_ReturnDefaultResult_AndTheException_WhenTheFunctionThrows()
    {
        var thrown = new InvalidOperationException("boom");

        var (result, exception) = Util.Try<int>(() => throw thrown);

        Assert.Equal(0, result);
        Assert.Same(thrown, exception);
    }
}
