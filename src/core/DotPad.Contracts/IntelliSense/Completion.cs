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
    Snippet, EnumMember, Constant, Struct, Event, Operator, TypeParameter, Reference
}

public interface ICompletionList
{
    IReadOnlyList<ICompletionItem> Items { get; }
    bool IsExclusive { get; }
}

public interface ICompletionItem
{
    string DisplayText { get; }
    string FilterText { get; }
    string SortText { get; }
    CompletionItemKind Kind { get; }

    // The member's signature, classified for syntax highlighting (e.g. `string Format(string format,
    // params object[] args)`) — shown above Summary in the completion tooltip.
    IReadOnlyList<DisplayTextSegment> Signature { get; }

    // Plain-text XML-doc <summary> (see cref/paramref already resolved to short names), or null if
    // the symbol has no XML documentation.
    string? Summary { get; }
}
