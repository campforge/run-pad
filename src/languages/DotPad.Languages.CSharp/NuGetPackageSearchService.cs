using System.Text.Json;
using DotPad.Contracts;

namespace DotPad.Languages.CSharp;

public sealed class NuGetPackageSearchService : IPackageSearchService, IDisposable
{
    private readonly HttpClient _http = new();
    private const string SearchEndpoint = "https://azuresearch-usnc.nuget.org/query";

    public async Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var url = $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&take=20&prerelease=false";

        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<PackageSearchResult>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            results.Add(new PackageSearchResult(
                Id: item.GetProperty("id").GetString() ?? "",
                Version: item.GetProperty("version").GetString() ?? "",
                Description: item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                TotalDownloads: item.TryGetProperty("totalDownloads", out var dl) ? dl.GetInt64() : 0));
        }

        return results;
    }

    public void Dispose() => _http.Dispose();
}
