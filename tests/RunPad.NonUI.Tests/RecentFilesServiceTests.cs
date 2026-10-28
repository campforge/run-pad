using RunPad.Services;
using Xunit;

namespace RunPad.NonUI.Tests;

public sealed class RecentFilesServiceTests
{
    [Fact]
    public void Remember_DeduplicatesMovesToFrontAndCapsAtMaximum()
    {
        var service = new RecentFilesService(Enumerable.Range(0, RecentFilesService.MaximumCount).Select(i => $"file-{i}"), StringComparer.Ordinal);

        service.Remember("file-20");
        service.Remember($"file-{RecentFilesService.MaximumCount}");

        Assert.Equal($"file-{RecentFilesService.MaximumCount}", service.Paths[0]);
        Assert.Equal("file-20", service.Paths[1]);
        Assert.Equal(RecentFilesService.MaximumCount, service.Paths.Count);
        Assert.Equal(1, service.Paths.Count(path => path == "file-20"));
    }

    [Fact]
    public void Constructor_DropsBlankEntriesAndClearRemovesAll()
    {
        var service = new RecentFilesService(["", "  ", "a", "a"], StringComparer.Ordinal);

        Assert.Equal(["a"], service.Paths);
        service.Clear();
        Assert.Empty(service.Paths);
    }
}
