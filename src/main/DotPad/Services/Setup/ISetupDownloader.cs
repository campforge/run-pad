namespace DotPad.Services.Setup;

// Thin seam around HttpClient so SetupInstallerService's orchestration logic can be unit tested with
// a fake — no real network access in tests.
public interface ISetupDownloader
{
    Task DownloadFileAsync(Uri url, string destinationPath, CancellationToken ct);
}
