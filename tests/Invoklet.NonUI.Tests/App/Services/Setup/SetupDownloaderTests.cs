using System.Net;
using Invoklet.Services.Setup;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Setup;

// Live report 2026-07-27 — the wizard's progress bar was permanently indeterminate because nothing
// ever measured real download progress. SetupDownloader now reads the response in chunks and reports
// bytes-read against Content-Length; these tests exercise that against a fake HttpMessageHandler
// (SetupDownloader's internal test-seam constructor) — no real network access.
public class SetupDownloaderTests
{
    [Fact]
    public async Task DownloadFileAsync_Should_ReportIncreasingPercent_UpTo100_WhenContentLengthIsKnown()
    {
        var content = new byte[250_000]; // several times SetupDownloader's 80 KiB read buffer, so this spans multiple chunks/reports
        Random.Shared.NextBytes(content);
        using var client = new HttpClient(new FakeHandler(content));
        var downloader = new SetupDownloader(client);
        var destinationPath = Path.Combine(Path.GetTempPath(), $"Invoklet-test-{Guid.NewGuid():N}.bin");
        var reported = new RecordingProgress<double>();

        try
        {
            await downloader.DownloadFileAsync(
                new Uri("https://example.invalid/file.bin"), destinationPath,
                reported, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(destinationPath));
            Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath, TestContext.Current.CancellationToken));
            Assert.True(reported.Values.Count > 1, "expected more than one progress report for a multi-chunk download");
            Assert.Equal(100, reported.Values[^1]);
            Assert.Equal(reported.Values.OrderBy(p => p), reported.Values);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    // No Content-Length means there's no total to measure bytes-read against — SetupWizardViewModel
    // relies on exactly this (never seeing a report) to know when to fall back to an indeterminate bar.
    [Fact]
    public async Task DownloadFileAsync_Should_NeverReportProgress_WhenContentLengthIsUnknown()
    {
        var content = "small body"u8.ToArray();
        using var client = new HttpClient(new FakeHandler(content, IncludeContentLength: false));
        var downloader = new SetupDownloader(client);
        var destinationPath = Path.Combine(Path.GetTempPath(), $"Invoklet-test-{Guid.NewGuid():N}.bin");
        var reported = new RecordingProgress<double>();

        try
        {
            await downloader.DownloadFileAsync(
                new Uri("https://example.invalid/file.bin"), destinationPath,
                reported, TestContext.Current.CancellationToken);

            Assert.Empty(reported.Values);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    // A plain, synchronous IProgress<T> — unlike the built-in Progress<T>, this doesn't dispatch
    // through a captured SynchronizationContext, so reports are guaranteed visible the instant
    // DownloadFileAsync's await returns instead of racing a thread-pool-queued callback.
    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class FakeHandler(byte[] content, bool IncludeContentLength = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            if (!IncludeContentLength)
                response.Content.Headers.ContentLength = null;
            return Task.FromResult(response);
        }
    }
}
