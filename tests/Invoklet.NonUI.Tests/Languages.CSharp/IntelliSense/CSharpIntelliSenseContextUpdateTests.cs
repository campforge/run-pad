using System.Collections.Concurrent;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Contracts.Models;
using Invoklet.Languages.CSharp;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense;

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

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // The older request is the slow one (its lookups were still running): it finishes after the
    // newer has been applied, and, not cancelled by anyone, must still not replace it.
    [Fact]
    public async Task ASlowOlderRequest_Should_NotOverwriteANewerOne_ThatFinishedFirst()
    {
        using var service = Create();
        using var olderHeld = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.ExecutionContextResolvedForTests = request =>
        {
            if (request.Connections?.Source.Contains("Stock", StringComparison.Ordinal) != true) return;
            olderHeld.Set();
            Assert.True(release.Wait(Patience));
        };

        var older = Task.Run(() => service.UpdateExecutionContext(Connection("Stock")), TestContext.Current.CancellationToken);
        Assert.True(await Task.Run(() => olderHeld.Wait(Patience), TestContext.Current.CancellationToken));
        service.UpdateExecutionContext(Connection("Audit"));
        release.Set();
        await older.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(await OffersAsync(service, "Audit"));
        Assert.False(await OffersAsync(service, "Stock"));
    }

    // The slow part of a request runs without the workspace lock: the editor keeps completing, with
    // the context it had, while a large contract is still being looked up.
    [Fact]
    public async Task ASlowContextRequest_Should_NotBlockCompletion()
    {
        using var service = Create();
        service.UpdateExecutionContext(Connection("Stock"));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.ExecutionContextResolvedForTests = _ =>
        {
            held.Set();
            Assert.True(release.Wait(Patience));
        };

        var slow = Task.Run(() => service.UpdateExecutionContext(Connection("Audit")), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await Task.Run(() => held.Wait(Patience), TestContext.Current.CancellationToken));
            Assert.True(await OffersAsync(service, "Stock").WaitAsync(Patience, TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
        }
        await slow.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(await OffersAsync(service, "Audit"));
    }

    // The schema is replaced while a completion has its snapshot: the list that is answered is the
    // newest solution's, never the replaced one's members.
    [Fact]
    public async Task ACompletionOvertakenByANewContext_Should_ListTheNewestMembers()
    {
        using var service = Create();
        service.UpdateExecutionContext(Connection("Stock"));
        using var snapshotTaken = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var calls = 0;
        service.CompletionSnapshotTakenForTests = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            snapshotTaken.Set();
            Assert.True(proceed.Wait(Patience));
        };

        var completion = OffersAndAvoidsAsync(service, "Audit", "Stock");
        Assert.True(await Task.Run(() => snapshotTaken.Wait(Patience), TestContext.Current.CancellationToken));
        service.UpdateExecutionContext(Connection("Audit"));
        proceed.Set();

        Assert.True(await completion.WaitAsync(Patience, TestContext.Current.CancellationToken));
    }

    private static async Task<bool> OffersAndAvoidsAsync(CSharpIntelliSenseService service, string offered, string avoided)
    {
        const string code = "Connections.";
        var completions = await service.GetCompletionsAsync(code, code.Length, ct: TestContext.Current.CancellationToken);
        return completions is not null
            && completions.Items.Any(i => i.DisplayText == offered)
            && completions.Items.All(i => i.DisplayText != avoided);
    }

    // Typing continues, then the context changes, while the first pass is still waiting to analyse:
    // what reaches the editor describes the last text on the newest solution, nothing older.
    [Fact]
    public async Task RapidTyping_WithAContextChange_Should_PublishOnlyTheNewestState()
    {
        using var service = Create();
        const string first = "Connections.Stock.ToString();";
        const string last = "var x = 1; Connections.Stock.ToString();";
        var published = new ConcurrentQueue<IReadOnlyList<ISemanticToken>>();
        var current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SemanticTokensChanged += (_, tokens) =>
        {
            published.Enqueue(tokens);
            if (tokens.Any(t => t is { StartOffset: 11, Length: 11, Kind: SemanticTokenKind.ClassName })) current.TrySetResult();
        };

        using var snapshotTaken = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var calls = 0;
        service.AnalysisSnapshotTakenForTests = () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            snapshotTaken.Set();
            Assert.True(proceed.Wait(Patience));
        };

        service.NotifyTextChanged(first);
        Assert.True(await Task.Run(() => snapshotTaken.Wait(Patience), TestContext.Current.CancellationToken));
        foreach (var text in new[] { "var ", "var x = 1; Conn", last }) service.NotifyTextChanged(text);
        service.UpdateExecutionContext(Connection("Stock"));
        proceed.Set();

        await current.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(published, tokens => tokens.Any(t => t is { StartOffset: 0, Length: 11, Kind: SemanticTokenKind.ClassName }));
    }

    private static bool IsClassColoured(IReadOnlyList<ISemanticToken> tokens) =>
        tokens.Any(t => t is { StartOffset: 0, Length: 11, Kind: SemanticTokenKind.ClassName });
}
