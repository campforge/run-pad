using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.DotnetCommon;

namespace Invoklet.Services;

/// <summary>What the navigation commands act on: the editor the caret is in.</summary>
internal interface INavigationTarget
{
    string Text { get; }

    int CaretOffset { get; }

    /// <summary>Puts the caret there, scrolls to it and focuses the editor.</summary>
    void JumpTo(int offset);

    /// <summary>Lets the user pick one of several implementations.</summary>
    void ShowImplementations(IReadOnlyList<IImplementationLocation> implementations);

    /// <summary>Lets the user pick one of the occurrences of the symbol.</summary>
    void ShowReferences(IReadOnlyList<IDocumentHighlightSpan> references);
}

/// <summary>
/// Go to Definition / Go to Implementation and Find References of the symbol under the caret. Implementations
/// come first when the symbol has any; a definition in the script is a jump, one outside it is decompiled
/// (or taken from the source generated for it) and shown in the assembly explorer. A cancelled request ends with
/// <see cref="OperationCanceledException"/> before it acts on the editor or opens a window, so a request that was
/// replaced, or whose tab closed, leaves nothing behind.
/// </summary>
internal sealed class SymbolNavigator(
    IIntelliSenseService intelliSense,
    IWindowService windows,
    Func<IDefinitionResult, DecompiledMemberResult> decompile)
{
    public async Task GoToDefinitionAsync(INavigationTarget target, CancellationToken cancellationToken)
    {
        var text = target.Text;
        var caretOffset = target.CaretOffset;

        var implementations = await intelliSense.GetImplementationsAsync(text, caretOffset, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (implementations is { Count: > 0 })
        {
            if (implementations.Count == 1) target.JumpTo(implementations[0].Offset);
            else target.ShowImplementations(implementations);
            return;
        }

        var definition = await intelliSense.GetDefinitionAsync(text, caretOffset, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (definition is null) return;

        if (definition.IsInSource)
        {
            target.JumpTo(definition.Offset);
            return;
        }

        // A connection's generated message/client class, before any build has cached a compiled contract
        // assembly for it to decompile from: its source is already in hand, so it is shown directly.
        if (definition.GeneratedDocument is { } generated)
        {
            await windows.ShowDefinitionInAssemblyExplorerAsync(
                new DecompiledMemberResult(generated.Text, generated.Line, definition.DisplayName), definition);
            return;
        }

        DecompiledMemberResult decompiled;
        try
        {
            decompiled = await Task.Run(() => decompile(definition), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            decompiled = new DecompiledMemberResult($"// Failed to decompile: {ex.Message}", 1, definition.DisplayName);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await windows.ShowDefinitionInAssemblyExplorerAsync(decompiled, definition);
    }

    // The same set of occurrences the highlight-occurrences feature computes: in a single-file scratchpad
    // the references of a symbol and its highlights are the same, they are only shown differently.
    public async Task FindReferencesAsync(INavigationTarget target, CancellationToken cancellationToken)
    {
        var highlights = await intelliSense.GetDocumentHighlightsAsync(target.Text, target.CaretOffset, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (highlights is { Count: > 0 }) target.ShowReferences(highlights);
    }
}
