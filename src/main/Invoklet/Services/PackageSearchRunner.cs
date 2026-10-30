using Invoklet.Contracts;

namespace Invoklet.Services;

/// <summary>What a search that was not superseded came to: a page of results, or the error that stands in for it.</summary>
internal sealed record PackageSearchOutcome(PackageSearchPage Page, string? Error);

/// <summary>
/// One online package search: the debounce that waits out typing, the check that the search is still the wanted one before and after
/// the slow part, and the service call off the calling thread. A service that fails does not fail the search: it comes back as an
/// empty page and the message to show.
/// </summary>
internal sealed class PackageSearchRunner(IPackageSearchService search, TimeSpan debounce)
{
    /// <param name="isCurrent">Whether this search is still the wanted one (a newer query, or the window closing, ends it).</param>
    /// <param name="started">Called once the debounce is over and the search is about to run.</param>
    /// <returns>Null when the search was superseded or cancelled and nothing should be shown.</returns>
    public async Task<PackageSearchOutcome?> RunAsync(
        string query, PackageSearchOptions options, bool immediate, Func<bool> isCurrent, Action started, CancellationToken ct)
    {
        if (!immediate && debounce > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(debounce, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        if (!isCurrent()) return null;

        started();

        PackageSearchPage page;
        string? error = null;
        try
        {
            // Off the calling thread: the NuGet client does real synchronous work before its first await (config and credentials,
            // the service index, JSON) that otherwise stalls typing in the box.
            page = await Task.Run(() => search.SearchAsync(query, options, ct), CancellationToken.None);
        }
        catch (Exception ex)
        {
            page = new PackageSearchPage([], HasMore: false);
            error = $"Search failed: {ex.Message}";
        }

        return isCurrent() ? new PackageSearchOutcome(page, error) : null;
    }
}
