namespace RunPad.Contracts.IntelliSense;

public readonly record struct CompletionTrigger(CompletionTriggerKind Kind, char Character = '\0')
{
    public static CompletionTrigger Invoked => new(CompletionTriggerKind.Invoked);
    public static CompletionTrigger Insertion(char ch) => new(CompletionTriggerKind.Insertion, ch);
}

public enum CompletionTriggerKind { Invoked, Insertion, Deletion }

public enum CompletionItemKind
{
    Text, Method, ExtensionMethod, Function, Constructor, Field, Variable,
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
    // to hardcode Kind == Method ? 1.0 : 0.9).
    double Priority { get; }

    // The member's signature, classified for syntax highlighting (e.g. `string Format(string format,
    // params object[] args)`) — shown above Summary in the completion tooltip.
    IReadOnlyList<DisplayTextSegment> Signature { get; }

    // Plain-text XML-doc <summary> (see cref/paramref already resolved to short names), or null if
    // the symbol has no XML documentation.
    string? Summary { get; }

    // Namespaces to add (as `using`/front-matter `namespaces:` entries — see EditorTabItemControl.
    // AddGlobalUsings) if this item is accepted. Empty for anything already in scope; non-empty for
    // a type found in an added reference that isn't imported yet — ReSharper-
    // style auto-import on completion accept, not just via the lightbulb.
    IReadOnlyList<string> UsingsToAdd { get; }

    // Short annotation shown next to DisplayText in the completion list row itself (not just the
    // Description tooltip) — e.g. "Newtonsoft.Json" for a type offered via UsingsToAdd, so the list
    // itself makes clear (ReSharper-style "(in Namespace)") that accepting it does more than just
    // insert text. Null for anything that needs no annotation.
    string? Detail { get; }

    // How many overloads share this DisplayText and were collapsed down to this single item —
    // 1 means "just this one, nothing else hidden". A method name with several
    // overloads only ever shows up once in the list (the language service already picks one
    // representative per name), so without this the list gave no indication there was anything else
    // to see under that name at all.
    int OverloadCount { get; }

    // True for a member declared directly on the type being completed (the receiver's type for a
    // dot-completion, or the type containing the current scope for an unqualified reference) — false
    // for an inherited member, a local/parameter/type/namespace/keyword, or anything else with no
    // "declared on this type" notion (ReSharper 1.1: own members render
    // bold, inherited ones don't).
    bool IsOwnMember { get; }

    // A multi-line snippet template (same `${0}`-cursor-marker/`\t`-indent syntax as
    // ILanguageHostFactory.Snippets) to insert instead of DisplayText — non-null only for an
    // override-member stub (typing "override " lists overridable base
    // members; accepting one inserts the full "<modifiers> override <returnType> <name>(<params>)
    // { <body> }" stub, not just the bare method name). Null for every ordinary completion item.
    string? InsertTemplate { get; }
}
