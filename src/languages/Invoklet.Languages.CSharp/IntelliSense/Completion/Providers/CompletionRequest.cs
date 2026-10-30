using Invoklet.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Invoklet.Languages.CSharp.IntelliSense.CompletionItems;
using static Invoklet.Languages.CSharp.IntelliSense.CompletionKeywords;
using static Invoklet.Languages.CSharp.IntelliSense.ExpectedTypeCompletion;
using static Invoklet.Languages.CSharp.IntelliSense.ImportableTypes;
using static Invoklet.Languages.CSharp.IntelliSense.ObjectInitializerCompletions;
using static Invoklet.Languages.CSharp.IntelliSense.OverrideStubs;

namespace Invoklet.Languages.CSharp.IntelliSense;

// One completion request: the document the caret is in, read once and shared by the providers.
internal sealed class CompletionRequest(
    string code, int position, CompletionTrigger trigger, SemanticModel model, SyntaxNode root, int documentOffset)
{
    private ExpectedTypeCompletion.ExpectedTypeContext? _expectedType;
    private bool _expectedTypeRead;

    // The caret in the user's code and in the document Roslyn sees (behind the preamble).
    public string Code => code;
    public int Position => position;
    public int Offset => documentOffset;
    public CompletionTrigger Trigger => trigger;
    public SemanticModel Model => model;
    public SyntaxNode Root => root;

    public string BeforeCaret => code[..Math.Min(position, code.Length)];

    public bool IsDotInsertion => trigger.Kind == CompletionTriggerKind.Insertion && trigger.Character == '.';

    // What type the code wants at the caret, read only by the providers that need it.
    public ExpectedTypeCompletion.ExpectedTypeContext? ExpectedType
    {
        get
        {
            if (!_expectedTypeRead)
            {
                _expectedType = ExpectedTypeCompletion.GetExpectedTypeContext(model, root, documentOffset);
                _expectedTypeRead = true;
            }
            return _expectedType;
        }
    }
}
