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
    string? Description { get; }
}
