using System.Threading.Tasks;

namespace DotPad.Avalonia.CodeTextEditor.Shared;

public interface ICodeEditorCompletionProvider
{
    Task<CompletionResult> GetCompletionData(int position, char? triggerChar, bool useSignatureHelp);
}