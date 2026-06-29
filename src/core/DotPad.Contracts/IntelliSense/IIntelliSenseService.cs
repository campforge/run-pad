namespace DotPad.Contracts.IntelliSense;

public interface IIntelliSenseService : IDisposable
{
    Task<ICompletionList?> GetCompletionsAsync(
        string code,
        int position,
        CompletionTrigger trigger = default,
        CancellationToken cancellationToken = default);

    Task<ISignatureHelpResult?> GetSignatureHelpAsync(
        string code,
        int position,
        char? triggerCharacter = null,
        CancellationToken cancellationToken = default);

    Task<IQuickInfoResult?> GetQuickInfoAsync(
        string code,
        int position,
        CancellationToken cancellationToken = default);
}
