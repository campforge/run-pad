using System.Runtime.CompilerServices;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp.IntelliSense.Completion.Providers;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Invoklet.Languages.DotnetCommon;
using Microsoft.Extensions.Logging;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion;

/// <summary>What the editor offers at the caret: the symbols in scope or of a receiver, keywords, the types to import, the members of an
/// object initializer, the stubs of overrides, the typed <c>Arguments</c> and the values an argument can take. The pieces are providers
/// asked in turn; the first that answers wins.</summary>
internal sealed class CompletionService(
    ScriptDocumentSession session, Func<string, Microsoft.CodeAnalysis.Document> syncDocument, ScriptDocumentLayout layout, ScriptContext context, ILogger logger)
{
    private readonly ScriptDocumentSession _session = session;
    private readonly Func<string, Microsoft.CodeAnalysis.Document> _syncDocument = syncDocument;
    private readonly ScriptDocumentLayout _layout = layout;
    private readonly ScriptContext _context = context;
    private readonly ILogger _logger = logger;

    private readonly ICompletionProvider[] _providers =
    [
        new ArgumentsContractProvider(context),
        new OverrideStubProvider(),
        new NoCompletionProvider(),
        new MemberAccessProvider(),
        new ObjectInitializerProvider(),
        new IdentifierProvider(context),
    ];

    private SimpleCompletionList? ProvideCompletions(CompletionRequest request)
    {
        foreach (var provider in _providers)
        {
            if (provider.Provide(request) is not { } list) continue;
            return list.Items.Count > 0 ? list : null;
        }
        return null;
    }

    // Test-only: called once a completion holds its document snapshot, before it reads it.
    internal Action? SnapshotTakenForTests { get; set; }

    internal Task<ICompletionList?> GetCompletionsAsync(
        string code, int position, CompletionTrigger trigger = default, CancellationToken ct = default)
        => RoslynMessageCulture.RunAsync(async () => (ICompletionList?)await TryGetCompletionsAsync(code, position, trigger, ct), ct);

    // A completion that read its solution before the references or generated classes changed lists
    // the old ones (a connection's members that are gone, none of the new): it is computed again on
    // the new solution, not shown. A few rounds at most, so a context that keeps changing cannot
    // keep a completion from ever answering.
    private async Task<ICompletionList?> TryGetCompletionsAsync(
        string code, int position, CompletionTrigger trigger, CancellationToken ct)
    {
        var version = new StrongBox<int>();
        for (var attempt = 0; ; attempt++)
        {
            var list = await ComputeCompletionsAsync(code, position, trigger, version, ct);
            if (attempt == MaxCompletionAttempts - 1 || _session.IsCurrent(version.Value)) return list;
            ct.ThrowIfCancellationRequested();
        }
    }

    private const int MaxCompletionAttempts = 3;

    private async Task<ICompletionList?> ComputeCompletionsAsync(
        string code, int position, CompletionTrigger trigger, StrongBox<int> contextVersion, CancellationToken ct)
    {
        try
        {
            Microsoft.CodeAnalysis.Document document;
            lock (_session.SyncLock)
            {
                document = _syncDocument(code);
                contextVersion.Value = _session.ContextVersion;
            }
            SnapshotTakenForTests?.Invoke();
            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) return null;

            var root = await document.GetSyntaxRootAsync(ct);
            if (root == null) return null;

            var pos = _layout.ToDocumentOffset(code, position);
            return ProvideCompletions(new CompletionRequest(code, position, trigger, model, root, pos));
        }
        // A superseded request (see CodeTextEditor.ShowCompletion) cancels its token —
        // that's an expected outcome, not a failure, so it must propagate instead of being swallowed by
        // the general catch below (which would otherwise log it as a spurious warning on every fast
        // keystroke that supersedes an in-flight request).
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Completions failed"); return null; }
    }
}
