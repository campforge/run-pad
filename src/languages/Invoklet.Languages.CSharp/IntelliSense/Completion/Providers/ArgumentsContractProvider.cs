using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion.Providers;

// `Arguments.` lists the script's argument contract straight from its metadata, so it works
// before a compile has produced the generated Arguments class.
internal sealed class ArgumentsContractProvider(ScriptContext context) : ICompletionProvider
{
    private const string Root = "Arguments.";

    public SimpleCompletionList? Provide(CompletionRequest request)
    {
        var beforeCaret = request.BeforeCaret;
        var start = beforeCaret.LastIndexOf(Root, StringComparison.Ordinal);
        if (start < 0 || (start > 0 && char.IsLetterOrDigit(beforeCaret[start - 1]))) return null;
        var typed = beforeCaret[(start + Root.Length)..];
        if (!typed.All(char.IsLetterOrDigit)) return null;

        var items = context.ArgumentContract
            .Where(m => m.PropertyName.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Select(m => (ICompletionItem)new SimpleCompletionItem(
                m.PropertyName, CompletionItemKind.Property,
                [new DisplayTextSegment(m.PropertyName, SemanticTokenKind.PropertyName)],
                $"{m.PropertyName} : {(m.Type == "enum" ? m.EnumTypeName : m.Type)}", m.PropertyName, 1, [],
                Source: CompletionSourceKind.Invoklet))
            .ToList();
        // A contract with no such member leaves the place to Roslyn (a user's own `Arguments`).
        return items.Count > 0 ? new SimpleCompletionList(items, CompletionContextKind.ArgumentsContract) : null;
    }
}
