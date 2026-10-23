using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RunPad.Contracts.Connections;
using RunPad.Contracts.IntelliSense;
using RunPad.Contracts.Models;
using RunPad.Languages.CSharp;
using RunPad.Languages.CSharp.IntelliSense;
using Xunit;

namespace RunPad.NonUI.Tests;

// The editor's execution context (references, generated connection classes) is replaced from a
// worker thread while the user keeps typing: a request that is overtaken must not be applied over
// the newer one, and analysis of an obsolete solution must not reach the editor.
public sealed class CSharpIntelliSenseContextUpdateTests
{
    private static CSharpIntelliSenseService Create()
    {
        var references = new CSharpReferenceService();
        return new CSharpIntelliSenseService(
            references.GetDefault().Append(references.GetExtensionReference()), NullLogger<CSharpIntelliSenseService>.Instance)
        {
            BackgroundAnalysisDebounce = TimeSpan.Zero,
        };
    }

    private static ExecutionRequest Connection(string member) => new()
    {
        Connections = new GeneratedConnections(
            $"public static partial class Connections {{ public static int {member} => 1; }}", [], []),
    };

    private static async Task<bool> OffersAsync(CSharpIntelliSenseService service, string member)
    {
        const string code = "Connections.";
        var completions = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);
        return completions?.Items.Any(i => i.DisplayText == member) == true;
    }

    [Fact]
    public async Task ACancelledRequest_Should_ChangeNothing()
    {
        using var service = Create();
        service.UpdateExecutionContext(Connection("Stock"));
        Assert.True(await OffersAsync(service, "Stock"));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => service.ApplyExecutionContext(Connection("Audit"), cancelled.Token));

        Assert.True(await OffersAsync(service, "Stock"));
        Assert.False(await OffersAsync(service, "Audit"));
    }

    // The pass has its solution snapshot when the classes of "Connections" arrive. What it computes
    // describes a solution without them (an unknown name has no class colour), so it must not be
    // published; the pass runs again on the new solution instead.
    [Fact]
    public async Task AnalysisOfAnObsoleteSolution_Should_NotBePublished_AndRunAgain()
    {
        using var service = Create();
        const string code = "Connections.Stock.ToString();";
        var published = new ConcurrentQueue<IReadOnlyList<ISemanticToken>>();
        var current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SemanticTokensChanged += (_, tokens) =>
        {
            published.Enqueue(tokens);
            if (IsClassColoured(tokens)) current.TrySetResult();
        };

        var snapshotTaken = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        var calls = 0;
        service.AnalysisSnapshotTakenForTests = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            snapshotTaken.Set();
            Assert.True(proceed.Wait(TimeSpan.FromSeconds(30)));
        };

        service.NotifyTextChanged(code);
        Assert.True(await Task.Run(() => snapshotTaken.Wait(TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken));
        service.UpdateExecutionContext(new ExecutionRequest
        {
            Connections = new GeneratedConnections("public static partial class Connections { public static int Stock => 1; }", [], []),
        });
        proceed.Set();

        await current.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.All(published, tokens => Assert.True(IsClassColoured(tokens), "an obsolete analysis was published"));
    }

    private static bool IsClassColoured(IReadOnlyList<ISemanticToken> tokens) =>
        tokens.Any(t => t is { StartOffset: 0, Length: 11, Kind: SemanticTokenKind.ClassName });
}
