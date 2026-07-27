namespace RunPad.Services.Setup;

public sealed class SetupDownloader : ISetupDownloader, IDisposable
{
    // 80 KiB — matches Stream.CopyToAsync's own default buffer size; only spelled out here because
    // manual chunked reads (instead of just calling CopyToAsync) are what let us report percent
    // between chunks in the first place.
    private const int BufferSize = 81920;

    private readonly HttpClient _client;

    public SetupDownloader() : this(new HttpClient())
    {
    }

    // Internal seam so SetupDownloaderTests can exercise the real chunked-read/percent-reporting logic
    // against a fake HttpMessageHandler instead of a real network call — DI (App.axaml.cs) only ever
    // sees the public parameterless constructor above.
    internal SetupDownloader(HttpClient client) => _client = client;

    public async Task DownloadFileAsync(Uri url, string destinationPath, IProgress<double>? percent, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destinationPath);

        var buffer = new byte[BufferSize];
        long bytesReadSoFar = 0;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            bytesReadSoFar += bytesRead;
            if (totalBytes is > 0)
                percent?.Report(bytesReadSoFar * 100.0 / totalBytes.Value);
        }
    }

    public void Dispose() => _client.Dispose();
}
