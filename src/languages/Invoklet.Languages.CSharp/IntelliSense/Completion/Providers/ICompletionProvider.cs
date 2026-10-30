namespace Invoklet.Languages.CSharp.IntelliSense.Completion.Providers;

internal interface ICompletionProvider
{
    // Null when the caret is not in this provider's kind of place, so the next one is asked; a
    // list, even an empty one, answers the request.
    SimpleCompletionList? Provide(CompletionRequest request);
}
