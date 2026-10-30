using Invoklet.Services;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

// RunPreflight has no injected dependencies; what it asks of the tab comes in as delegates, which the fixture
// records so a test can say what was asked and in which order.
internal sealed class RunPreflightFixture
{
    public List<string> Calls { get; } = [];

    public InPlaceSaveResult SaveResult { get; set; } = InPlaceSaveResult.Saved;

    public bool MustAbort { get; set; }

    public string? ArgumentsError { get; set; }

    public static RunPreflightInput CreateInput(string? parseError = null, string? filePath = "/scripts/a.inv", bool isSelection = false)
        => new(parseError, filePath, isSelection);

    public Func<Task<InPlaceSaveResult>> Save => () =>
    {
        Calls.Add("save");
        return Task.FromResult(SaveResult);
    };

    public Func<bool> Abort => () =>
    {
        Calls.Add("abort?");
        return MustAbort;
    };

    public Func<string?> Arguments => () =>
    {
        Calls.Add("arguments");
        return ArgumentsError;
    };

    public RunPreflight CreateSut() => new();
}
