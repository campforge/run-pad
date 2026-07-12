namespace DotPad.Contracts.IntelliSense;

public readonly record struct CompletionTrigger(CompletionTriggerKind Kind, char Character = '\0')
{
    public static CompletionTrigger Invoked => new(CompletionTriggerKind.Invoked);
    public static CompletionTrigger Insertion(char ch) => new(CompletionTriggerKind.Insertion, ch);
}

public enum CompletionTriggerKind { Invoked, Insertion, Deletion }

public enum CompletionItemKind
{
    Text, Method, Function, Constructor, Field, Variable,
    Class, Interface, Module, Property, Enum, Keyword,
    Snippet, EnumMember, Constant, Struct, Event, Operator, TypeParameter, Reference, Delegate
}

public interface ICompletionList
{
    IReadOnlyList<ICompletionItem> Items { get; }
}

public interface ICompletionItem
{
    string DisplayText { get; }
    string SortText { get; }
    CompletionItemKind Kind { get; }

    // Tie-break used by the editor's completion list when two items match the typed prefix equally
    // well (e.g. both are exact prefix matches) — higher wins. Mirrors the same in-scope-first
    // ranking as SortText (locals/parameters > members > types > namespaces > keywords), computed by
    // the language model rather than hardcoded by the UI layer (see EditorTabItemControl, which used
    // to hardcode Kind == Method ? 1.0 : 0.9 — ANALYSIS.md #55).
    double Priority { get; }

    // The member's signature, classified for syntax highlighting (e.g. `string Format(string format,
    // params object[] args)`) — shown above Summary in the completion tooltip.
    IReadOnlyList<DisplayTextSegment> Signature { get; }

    // Plain-text XML-doc <summary> (see cref/paramref already resolved to short names), or null if
    // the symbol has no XML documentation.
    string? Summary { get; }

    // Namespaces to add (as `using`/front-matter `namespaces:` entries — see EditorTabItemControl.
    // AddGlobalUsings) if this item is accepted. Empty for anything already in scope; non-empty for
    // a type found in an added reference that isn't imported yet (TODO.md #28 remainder) — ReSharper-
    // style auto-import on completion accept, not just via the lightbulb.
    IReadOnlyList<string> UsingsToAdd { get; }
}
