using AvaloniaEdit.CodeCompletion;
using Invoklet.Avalonia.CodeTextEditor.Completions;
using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Services;

/// <summary>Turns an item of the language service into the row the editor's completion window shows; builds controls.</summary>
internal interface ICompletionItemFactory
{
    ICompletionData Create(ICompletionItem item);
}

/// <summary>
/// Answers the editor's completion requests from the language service: what the request asks for, what to do
/// when the service finds the place not worth completing at, and the items otherwise.
/// </summary>
internal sealed class EditorCompletionProvider(
    IIntelliSenseService intelliSense, Func<string> text, ICompletionItemFactory items)
{
    public async Task ProvideAsync(CompletionRequestedEventArgs e)
    {
        // A genuine Ctrl+Space (not the automatic triggers, which also set IsExplicitInvoke but pass the typed
        // character as Trigger, never '\0') in a fresh, not-yet-typed call argument asks for parameter info, not a
        // completion list, as in Rider and VS. Gated on the literal hotkey so this extra signature-help probe
        // never runs on the hot path of typing.
        if (e.IsExplicitInvoke && e.Trigger == '\0')
        {
            var signatureHelp = await intelliSense.GetSignatureHelpAsync(text(), e.CaretOffset, cancellationToken: e.CancellationToken);
            if (signatureHelp is { IsArgumentSlotEmpty: true })
            {
                e.RedirectToOverloads = true;
                return;
            }
        }

        var trigger = e.IsExplicitInvoke ? CompletionTrigger.Invoked : CompletionTrigger.Insertion(e.Trigger);
        var list = await intelliSense.GetCompletionsAsync(text(), e.CaretOffset, trigger, e.CancellationToken);
        if (list is null)
        {
            // The service found this place not worth completing at (inside a comment or a string literal, for
            // one), so the snippets must not fill the list either.
            e.SuppressSnippets = true;
            return;
        }

        foreach (var item in list.Items) e.Items.Add(items.Create(item));
    }
}
