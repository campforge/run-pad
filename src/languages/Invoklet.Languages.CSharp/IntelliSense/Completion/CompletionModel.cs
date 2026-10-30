using Invoklet.Contracts.IntelliSense;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>The kind of place in the code a completion list was built for.</summary>
internal enum CompletionContextKind
{
    /// <summary>Nothing completes here: a comment, a string, a name being declared, a dot after nothing.</summary>
    None,
    /// <summary>`Arguments.`, from the script's argument contract.</summary>
    ArgumentsContract,
    /// <summary>After `override `: stubs for the base members still open to override.</summary>
    OverrideStub,
    /// <summary>`Namespace.`</summary>
    NamespaceMember,
    /// <summary>`value.` or `value?.`</summary>
    InstanceMember,
    /// <summary>`Type.`</summary>
    StaticMember,
    /// <summary>`Arguments.`, `Connections.` or `Util.` when it resolves to the generated class.</summary>
    GeneratedApiMember,
    /// <summary>A member name in `new Type { | }`.</summary>
    ObjectInitializerMember,
    /// <summary>Inside `List&lt;|&gt;`.</summary>
    TypeArgument,
    /// <summary>A value where the code says what type it wants: an argument, an initializer value.</summary>
    ExpectedValue,
    /// <summary>The type after `new` where the code says what type it wants.</summary>
    ExpectedNewType,
    /// <summary>Any other name.</summary>
    Identifier,
}

/// <summary>Where a completion item came from.</summary>
internal enum CompletionSourceKind
{
    /// <summary>A symbol Roslyn sees in scope.</summary>
    Roslyn,
    /// <summary>Invoklet's own script API: the argument contract, generated classes, Invoklet.Runtime's extension methods.</summary>
    Invoklet,
    /// <summary>A C# keyword.</summary>
    Keyword,
    /// <summary>Suggested by the type the code wants: `new Type`, an enum member, a literal.</summary>
    ExpectedType,
    /// <summary>`name:` for a parameter of the call the caret is in.</summary>
    NamedArgument,
    /// <summary>A type from a package that is not imported yet.</summary>
    ImportableType,
    /// <summary>Inserted as a template, such as an override stub.</summary>
    Template,
}

internal sealed record SimpleCompletionItem(
    string DisplayText, CompletionItemKind Kind, IReadOnlyList<DisplayTextSegment> Signature,
    string? Summary, string SortText, double Priority, IReadOnlyList<string> UsingsToAdd,
    string? Detail = null, int OverloadCount = 1, bool IsOwnMember = false,
    string? InsertTemplate = null, CompletionSourceKind Source = CompletionSourceKind.Roslyn) : ICompletionItem;

internal sealed class SimpleCompletionList(IReadOnlyList<ICompletionItem> items, CompletionContextKind context) : ICompletionList
{
    public IReadOnlyList<ICompletionItem> Items => items;

    public CompletionContextKind Context => context;
}
