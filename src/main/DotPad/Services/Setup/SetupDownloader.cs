namespace DotPad.Services.Setup;

public sealed class SetupDownloader : ISetupDownloader, IDisposable
{
    private readonly HttpClient _client = new();

    public async Task DownloadFileAsync(Uri url, string destinationPath, CancellationToken ct)
    {
        await using var response = await _client.GetStreamAsync(url, ct);
        await using var file = File.Create(destinationPath);
        await response.CopyToAsync(file, ct);
    }

    public void Dispose() => _client.Dispose();
}
