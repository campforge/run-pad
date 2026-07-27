namespace RunPad.Services.Setup;

// Thin seam around HttpClient so SetupInstallerService's orchestration logic can be unit tested with
// a fake — no real network access in tests.
public interface ISetupDownloader
{
    // percent is 0-100 when the server sends a Content-Length, otherwise progress is never reported
    // (a determinate ProgressBar can't be driven off an unknown total) — callers fall back to an
    // indeterminate display when they never see a report.
    Task DownloadFileAsync(Uri url, string destinationPath, IProgress<double>? percent, CancellationToken ct);
}
